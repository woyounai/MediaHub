using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

record HlsTransferProgress(long Bytes, int Completed, int Total, int Active);

// Parallelizes finite MPEG-TS HLS, including AES-128 key rotation. More complex
// manifests deliberately stay on the existing FFmpeg compatibility path.
static class HlsParallelDownloader
{
    internal const string CacheName = ".mediahub-parts";
    private sealed record Segment(Uri Uri, double Duration, Uri? Key, byte[] Iv, bool Discontinuity);
    private static Uri NetworkUri(Uri parent, string value)
    {
        var uri = new Uri(parent, value);
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) throw new IOException("Unsupported media URI");
        return uri;
    }
    private static Dictionary<string, string> Attributes(string line) => Regex.Matches(line[(line.IndexOf(':') + 1)..], "(?:^|,)([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)")
        .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'), StringComparer.Ordinal);

    public static async Task<string?> PrepareAsync(string source, string saveDir, int threads, string? proxy,
        IReadOnlyDictionary<string, string> headers,
        Func<HlsTransferProgress, Task> progress, CancellationToken token)
    {
        if (proxy?.StartsWith("socks5:", StringComparison.OrdinalIgnoreCase) == true) return null;
        var handler = new SocketsHttpHandler { MaxConnectionsPerServer = threads, ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrWhiteSpace(proxy)) { handler.UseProxy = true; handler.Proxy = new WebProxy(proxy); }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        foreach (var (name, value) in headers) { if (!client.DefaultRequestHeaders.TryAddWithoutValidation(name, value)) throw new IOException("Invalid request header"); }
        if (!headers.ContainsKey("User-Agent")) client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        var uri = NetworkUri(new Uri(source), source);
        string manifest = "";
        for (var depth = 0; depth < 5; depth++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            uri = response.RequestMessage!.RequestUri!;
            if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) throw new IOException("Manifest too large");
            using var body = new MemoryStream();
            await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(token), body, 4 * 1024 * 1024, token);
            manifest = Encoding.UTF8.GetString(body.ToArray()).TrimStart('\uFEFF');
            var lines = manifest.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.FirstOrDefault() != "#EXTM3U") throw new IOException("Invalid HLS manifest");
            if (lines.Any(l => l.StartsWith("#EXT-X-MEDIA:"))) return null;
            if (!lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:"))) break;
            var variants = new List<(long Bandwidth, Uri Uri)>();
            for (var i = 0; i < lines.Length - 1; i++)
                if (lines[i].StartsWith("#EXT-X-STREAM-INF:") && !lines[i + 1].StartsWith('#'))
                {
                    var attributes = Attributes(lines[i]);
                    if (attributes.ContainsKey("AUDIO")) return null;
                    variants.Add((long.TryParse(attributes.GetValueOrDefault("BANDWIDTH"), out var b) ? b : 0, NetworkUri(uri, lines[i + 1])));
                }
            if (variants.Count == 0 || depth == 4) return null;
            uri = variants.OrderByDescending(v => v.Bandwidth).First().Uri;
        }
        var rows = manifest.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (!rows.Contains("#EXT-X-ENDLIST") || rows.Any(l => l.StartsWith("#EXT-X-MAP:") || l.StartsWith("#EXT-X-BYTERANGE:") || l.StartsWith("#EXT-X-GAP") || l.StartsWith("#EXT-X-I-FRAMES-ONLY"))) return null;
        var segments = new List<Segment>();
        ulong sequence = 0; Uri? key = null; byte[]? iv = null; double? duration = null; var discontinuity = false;
        foreach (var row in rows)
        {
            if (row.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) sequence = ulong.Parse(row[22..], CultureInfo.InvariantCulture);
            else if (row.StartsWith("#EXT-X-KEY:"))
            {
                var attributes = Attributes(row);
                if (attributes.GetValueOrDefault("METHOD") == "NONE") { key = null; iv = null; }
                else if (attributes.GetValueOrDefault("METHOD") == "AES-128" && (!attributes.TryGetValue("KEYFORMAT", out var format) || format == "identity"))
                {
                    key = NetworkUri(uri, attributes["URI"]);
                    iv = null;
                    if (attributes.TryGetValue("IV", out var value))
                    {
                        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
                        if (hex.Length > 32) throw new IOException("Invalid IV");
                        iv = Convert.FromHexString(hex.PadLeft(32, '0'));
                    }
                }
                else return null;
            }
            else if (row.StartsWith("#EXTINF:"))
            {
                if (!double.TryParse(row[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d) || d <= 0) throw new IOException("Invalid duration");
                duration = d;
            }
            else if (row == "#EXT-X-DISCONTINUITY") discontinuity = true;
            else if (!row.StartsWith('#'))
            {
                if (duration is null || segments.Count >= 100000) throw new IOException("Invalid segment list");
                var actualIv = iv ?? new byte[16];
                if (iv is null) BinaryPrimitives.WriteUInt64BigEndian(actualIv.AsSpan(8), sequence);
                segments.Add(new(NetworkUri(uri, row), duration.Value, key, actualIv, discontinuity));
                sequence++; duration = null; discontinuity = false;
            }
        }
        if (segments.Count == 0) throw new IOException("Empty HLS manifest");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri + manifest)));
        var cache = PathGuard.EnsureAllowed(Path.Combine(saveDir, CacheName, fingerprint));
        Directory.CreateDirectory(cache);
        var keys = new ConcurrentDictionary<string, Lazy<Task<byte[]>>>();
        long bytes = 0; var completed = 0; var active = 0;
        // Serialize callbacks so a late progress frame cannot lower the counters.
        using var reportGate = new SemaphoreSlim(1);
        async Task Report()
        {
            await reportGate.WaitAsync(token);
            try { await progress(new(Interlocked.Read(ref bytes), Volatile.Read(ref completed), segments.Count, Volatile.Read(ref active))); }
            finally { reportGate.Release(); }
        }
        await Report();
        await Parallel.ForEachAsync(Enumerable.Range(0, segments.Count), new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, async (index, ct) =>
        {
            var segment = segments[index];
            var final = PathGuard.EnsureAllowed(Path.Combine(cache, $"{index:D8}.ts"));
            if (File.Exists(final)) { Interlocked.Add(ref bytes, new FileInfo(final).Length); Interlocked.Increment(ref completed); await Report(); return; }
            Interlocked.Increment(ref active); await Report();
            try
            {
                var partial = PathGuard.EnsureAllowed(final + ".partial");
                var encrypted = PathGuard.EnsureAllowed(final + ".encrypted");
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var response = await client.GetAsync(segment.Uri, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        await using (var target = File.Create(segment.Key is null ? partial : encrypted))
                            await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(ct), target, 256L * 1024 * 1024, ct);
                        if (segment.Key is not null)
                        {
                            var keyBytes = await keys.GetOrAdd(segment.Key.AbsoluteUri, _ => new Lazy<Task<byte[]>>(async () =>
                            {
                                using var keyResponse = await client.GetAsync(segment.Key, HttpCompletionOption.ResponseHeadersRead, token);
                                keyResponse.EnsureSuccessStatusCode();
                                using var keyData = new MemoryStream();
                                await CopyBoundedAsync(await keyResponse.Content.ReadAsStreamAsync(token), keyData, 16, token);
                                if (keyData.Length != 16) throw new IOException("Invalid AES key");
                                return keyData.ToArray();
                            })).Value;
                            using var aes = Aes.Create(); aes.Key = keyBytes; aes.IV = segment.Iv;
                            await using var input = File.OpenRead(PathGuard.EnsureAllowed(encrypted));
                            await using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
                            await using var output = File.Create(PathGuard.EnsureAllowed(partial));
                            await crypto.CopyToAsync(output, ct);
                        }
                        ct.ThrowIfCancellationRequested();
                        File.Move(PathGuard.EnsureAllowed(partial), PathGuard.EnsureAllowed(final), false);
                        if (File.Exists(encrypted)) File.Delete(PathGuard.EnsureAllowed(encrypted));
                        break;
                    }
                    catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && (ex is HttpRequestException or IOException or TaskCanceledException)) { await Task.Delay(500 * (attempt + 1), ct); }
                }
                Interlocked.Add(ref bytes, new FileInfo(final).Length); Interlocked.Increment(ref completed);
            }
            finally { Interlocked.Decrement(ref active); await Report(); }
        });
        // Only locally generated filenames are put into this playlist. No key URLs
        // or arbitrary paths reach FFmpeg's file-enabled protocol handler.
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:" + Math.Ceiling(segments.Max(s => s.Duration)).ToString(CultureInfo.InvariantCulture) + "\n");
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Discontinuity) playlist.AppendLine("#EXT-X-DISCONTINUITY");
            playlist.Append("#EXTINF:").Append(segments[i].Duration.ToString(CultureInfo.InvariantCulture)).Append(",\n").Append(i.ToString("D8")).Append(".ts\n");
        }
        playlist.AppendLine("#EXT-X-ENDLIST");
        var local = PathGuard.EnsureAllowed(Path.Combine(cache, "local.m3u8"));
        await File.WriteAllTextAsync(local, playlist.ToString(), new UTF8Encoding(false), token);
        return local;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken token)
    {
        await using (source)
        {
            var buffer = new byte[64 * 1024]; long total = 0; int read;
            while ((read = await source.ReadAsync(buffer, token)) != 0)
            {
                total += read; if (total > limit) throw new IOException("Resource too large");
                await target.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }
    }

    public static void CleanCompletedCache(string playlist, string saveDir)
    {
        try
        {
            var cache = PathGuard.EnsureAllowed(Path.GetDirectoryName(playlist)!);
            var root = PathGuard.EnsureAllowed(Path.Combine(saveDir, CacheName));
            if (!PathGuard.IsSameOrChild(cache, root) || Path.GetFileName(cache).Length != 64) return;
            foreach (var file in Directory.EnumerateFiles(cache))
                if (Path.GetFileName(file) == "local.m3u8" || Regex.IsMatch(Path.GetFileName(file), @"^\d{8}\.ts(\.partial|\.encrypted)?$")) File.Delete(PathGuard.EnsureAllowed(file));
            if (!Directory.EnumerateFileSystemEntries(cache).Any()) Directory.Delete(cache);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch { /* Cached files are excluded from indexing even if cleanup fails. */ }
    }
}
