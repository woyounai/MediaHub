using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.StaticFiles;

static class DlnaService
{
    record Device(string Id, string Name, Uri Control, string Service);
    record Ticket(string Path, DateTime Expires);
    public record PlayRequest(string DeviceId, string Path);
    public record LocalPlayRequest(string Path);
    static readonly ConcurrentDictionary<string, Device> Devices = new();
    static readonly ConcurrentDictionary<string, Ticket> Tickets = new();
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
    static readonly FileExtensionContentTypeProvider Types = new();
    public static void Map(WebApplication app, int port)
    {
        app.MapPost("/api/player/prepare", (LocalPlayRequest media, HttpRequest request) =>
        {
            try
            {
                var path = PathGuard.EnsureAllowed(media.Path);
                if (!File.Exists(path)) return Results.NotFound(new { error = "文件不存在。" });
                if (!Types.TryGetContentType(path, out var mime) || !(mime.StartsWith("audio/") || mime.StartsWith("video/"))) return Results.BadRequest(new { error = "请选择音频或视频文件。" });
                foreach (var old in Tickets) if (old.Value.Expires < DateTime.UtcNow) Tickets.TryRemove(old.Key, out _);
                var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                Tickets[token] = new(path, DateTime.UtcNow.AddHours(12));
                var origin = $"{request.Scheme}://{request.Host}";
                return Results.Ok(new { url = $"{origin}/dlna/media/{token}", playlist = $"{origin}/player/playlist/{token}", mime });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/player/playlist/{token}", (string token, HttpRequest request) =>
        {
            if (!Tickets.TryGetValue(token, out var ticket) || ticket.Expires < DateTime.UtcNow) return Results.NotFound();
            var url = $"{request.Scheme}://{request.Host}/dlna/media/{token}";
            return Results.File(Encoding.UTF8.GetBytes("#EXTM3U\n" + url + "\n"), "audio/x-mpegurl", "MediaHub.m3u");
        });
        app.MapGet("/api/dlna/devices", async () =>
        {
            try { await Discover(); return Results.Ok(new { devices = Devices.Values.Select(d => new { id = d.Id, name = d.Name }).ToArray() }); }
            catch (Exception ex) { return Results.BadRequest(new { error = "扫描失败：" + ex.Message }); }
        });
        app.MapPost("/api/dlna/play", async (PlayRequest request) =>
        {
            string? token = null;
            try
            {
                if (!Devices.TryGetValue(request.DeviceId, out var device)) return Results.BadRequest(new { error = "请重新扫描并选择设备。" });
                var path = PathGuard.EnsureAllowed(request.Path);
                if (!File.Exists(path)) return Results.NotFound(new { error = "媒体文件不存在。" });
                if (!Types.TryGetContentType(path, out var mime) || !(mime.StartsWith("audio/") || mime.StartsWith("video/") || mime.StartsWith("image/"))) return Results.BadRequest(new { error = "请选择视频、音频或图片文件。" });
                using var socket = new UdpClient(AddressFamily.InterNetwork);
                socket.Connect(device.Control.Host, device.Control.Port);
                var address = ((IPEndPoint)socket.Client.LocalEndPoint!).Address;
                foreach (var entry in Tickets) if (entry.Value.Expires < DateTime.UtcNow) Tickets.TryRemove(entry.Key, out _);
                token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                Tickets[token] = new(path, DateTime.UtcNow.AddHours(12));
                var url = $"http://{address}:{port}/dlna/media/{token}";
                XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/", dc = "http://purl.org/dc/elements/1.1/", upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
                var metadata = new XElement(didl + "DIDL-Lite", new XElement(didl + "item", new XAttribute("id", "1"), new XAttribute("parentID", "0"), new XAttribute("restricted", "1"), new XElement(dc + "title", Path.GetFileName(path)), new XElement(upnp + "class", mime.StartsWith("audio/") ? "object.item.audioItem.musicTrack" : mime.StartsWith("image/") ? "object.item.imageItem.photo" : "object.item.videoItem"), new XElement(didl + "res", new XAttribute("protocolInfo", $"http-get:*:{mime}:*"), url))).ToString(SaveOptions.DisableFormatting);
                await Soap(device, "SetAVTransportURI", new XElement("CurrentURI", url), new XElement("CurrentURIMetaData", metadata));
                await Soap(device, "Play", new XElement("Speed", "1"));
                return Results.Ok(new { ok = true, device = device.Name });
            }
            catch (Exception ex) { if (token is not null) Tickets.TryRemove(token, out _); return Results.BadRequest(new { error = "设备未接受播放：" + ex.Message }); }
        });
        app.MapGet("/dlna/media/{token}", (string token, HttpResponse response) =>
        {
            if (!Tickets.TryGetValue(token, out var ticket) || ticket.Expires < DateTime.UtcNow) return Results.NotFound();
            var path = PathGuard.EnsureAllowed(ticket.Path);
            if (!File.Exists(path)) return Results.NotFound();
            Types.TryGetContentType(path, out var mime);
            response.Headers["transferMode.dlna.org"] = "Streaming";
            return Results.File(path, mime ?? "application/octet-stream", enableRangeProcessing: true);
        });
    }
    static async Task Discover()
    {
        var locations = new HashSet<Uri>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var message = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n");
        await udp.SendAsync(message, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                var location = Encoding.UTF8.GetString(packet.Buffer).Split("\r\n").FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == "http" && IPAddress.TryParse(uri.Host, out var host) && host.Equals(packet.RemoteEndPoint.Address)) locations.Add(uri);
            }
        }
        catch (OperationCanceledException) { }
        var found = await Task.WhenAll(locations.Select(async location =>
        {
            try
            {
                var xml = XDocument.Parse(await Http.GetStringAsync(location));
                var service = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "service" && e.Elements().Any(x => x.Name.LocalName == "serviceType" && x.Value.Contains(":AVTransport:")));
                if (service is null) return null;
                var type = service.Elements().First(x => x.Name.LocalName == "serviceType").Value;
                var control = new Uri(location, service.Elements().First(x => x.Name.LocalName == "controlURL").Value);
                if (control.Host != location.Host || control.Scheme != "http") return null;
                return new Device(location.ToString(), xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value ?? location.Host, control, type);
            }
            catch { return null; }
        }));
        Devices.Clear();
        foreach (var device in found) if (device is not null) Devices[device.Id] = device;
    }
    static async Task Soap(Device device, string action, params XElement[] arguments)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var body = new XElement(soap + "Envelope", new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"), new XElement(soap + "Body", new XElement(XName.Get(action, device.Service), new XElement("InstanceID", "0"), arguments)));
        using var request = new HttpRequestMessage(HttpMethod.Post, device.Control) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{device.Service}#{action}\"");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new IOException($"{action} 返回 {(int)response.StatusCode}：{await response.Content.ReadAsStringAsync()}");
    }
}
