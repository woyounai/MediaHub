using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// The web-facing task model follows Motrix's task/status vocabulary. Engine RPC
// is deliberately private: clients can only invoke the operations below.
sealed class DownloadTaskRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "direct";
    public string Status { get; set; } = "queued";
    public string SaveDir { get; set; } = "";
    public string? Gid { get; set; }
    public string? TorrentPath { get; set; }
    public bool AutoIndex { get; set; } = true;
    public bool Indexed { get; set; }
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public long DownloadSpeed { get; set; }
    public double? Progress { get; set; }
    public int ActiveConnections { get; set; }
    public int ConnectionLimit { get; set; }
    public int CompletedSegments { get; set; }
    public int TotalSegments { get; set; }
    public string? DownloadMode { get; set; }
    public bool WaitingForSlot { get; set; }
    public int Priority { get; set; } = 0;
    public string? Proxy { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Details { get; set; }
    public string? DetailError { get; set; }
    public string? Error { get; set; }
    public string? IndexError { get; set; }
    public string[] Files { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

record CreateDownloadRequest(string Source, string Directory, string? Name, bool AutoIndex = true, string? Kind = null, string? Proxy = null, Dictionary<string, string>? Headers = null, int Priority = 0, string? UserAgent = null);
record DownloadSettings(int MaxConcurrentTasks = 3, int ConnectionsPerFile = 4, int HlsThreads = 8, long MaxDownloadSpeed = 0, string? Proxy = null, long ScheduledSpeedLimit = 0, string? ScheduleStart = null, string? ScheduleEnd = null);

sealed class DownloadService : BackgroundService
{
    public static bool IsLocalClient(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a =>
        {
            var local = a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address;
            return local.Equals(normalized);
        });
    }
    private static readonly ConcurrentDictionary<string, byte> Incomplete = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, DownloadTaskRecord> _tasks = [];
    private readonly Dictionary<string, Process> _media = [];
    private sealed class HlsRun { public CancellationTokenSource Cancellation { get; } = new(); public Task? Work { get; set; } }
    private readonly Dictionary<string, HlsRun> _hls = [];
    private readonly Dictionary<string, long> _lastHlsBytes = [];
    private readonly ConcurrentDictionary<string, (string Path, DateTime Expires)> _fileTickets = new(StringComparer.Ordinal);
    private DownloadSettings _settings = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HttpClient _rpc = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly MediaDb _db;
    private readonly string _data, _connection;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _aria = Path.Combine(AppContext.BaseDirectory, "tools", "aria2c.exe");
    private readonly string _ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    private Process? _engine;
    private int _rpcPort;
    private string? _engineError;
    private bool _stopping;
    private readonly DownloadProcessJob _job = new();

    public DownloadService(AppPaths paths, MediaDb db)
    {
        _db = db;
        _data = Path.Combine(paths.DataDirectory, "downloads");
        System.IO.Directory.CreateDirectory(_data);
        var settingsFile = Path.Combine(_data, "settings.json");
        if (File.Exists(settingsFile)) { _settings = JsonSerializer.Deserialize<DownloadSettings>(File.ReadAllText(settingsFile), Json) ?? new(); ValidateSettings(_settings); }
        _connection = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS DownloadTasks(Id TEXT PRIMARY KEY, Data TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT Data FROM DownloadTasks";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var task = JsonSerializer.Deserialize<DownloadTaskRecord>(reader.GetString(0), Json)!;
            if (task.Status is not ("completed" or "removed" or "error")) task.Status = "paused";
            task.DownloadSpeed = 0;
            task.ActiveConnections = 0;
            task.Gid = null; // New owned engine; do not attach to another process's tasks.
            _tasks.Add(task.Id, task);
            if (task.Progress != 1) Incomplete[task.SaveDir] = 0;
        }
    }

    public static bool IsIncompletePath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(HlsParallelDownloader.CacheName, StringComparer.OrdinalIgnoreCase) || Incomplete.Keys.Any(root => PathGuard.IsSameOrChild(path, root));
    private static void ValidateSettings(DownloadSettings settings)
    {
        if (settings.MaxConcurrentTasks is < 1 or > 8 || settings.ConnectionsPerFile is < 1 or > 16 || settings.HlsThreads is < 1 or > 32 || settings.MaxDownloadSpeed < 0 || settings.ScheduledSpeedLimit < 0 || settings.Proxy is { Length: > 2048 })
            throw new ArgumentException("下载设置无效：检查任务并发、连接数、限速值和代理地址。");
        if (!string.IsNullOrWhiteSpace(settings.Proxy) && (!Uri.TryCreate(settings.Proxy, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5"))) throw new ArgumentException("代理仅支持 HTTP、HTTPS 或 SOCKS5 地址。");
        if ((settings.ScheduleStart is null) != (settings.ScheduleEnd is null) || settings.ScheduleStart is not null && (!TimeOnly.TryParse(settings.ScheduleStart, out _) || !TimeOnly.TryParse(settings.ScheduleEnd, out _))) throw new ArgumentException("限速时段需填写有效的开始和结束时间。");
    }
    public async Task<DownloadSettings> SettingsAsync(DownloadSettings? settings = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (settings is null) return _settings;
            if (_stopping) throw new ArgumentException("下载服务正在停止。");
            ValidateSettings(settings);
            var file = Path.Combine(_data, "settings.json");
            await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(settings, Json));
            File.Move(file + ".tmp", file, true);
            _settings = settings;
            if (_engine is { HasExited: false }) await CallAsync("aria2.changeGlobalOption", new Dictionary<string, string> { ["max-overall-download-limit"] = settings.MaxDownloadSpeed > 0 ? settings.MaxDownloadSpeed.ToString(CultureInfo.InvariantCulture) : "0", ["all-proxy"] = settings.Proxy ?? "" });
            return _settings;
        }
        finally { _gate.Release(); }
    }
    private int ActiveTaskCount => _tasks.Values.Count(t => t.Status is "downloading" or "fetching_metadata" or "finalizing" || t.Status == "queued" && t.Gid is not null && !t.WaitingForSlot);
    private SqliteConnection Open() { var c = new SqliteConnection(_connection); c.Open(); return c; }
    private void Save(DownloadTaskRecord task)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO DownloadTasks VALUES($id,$data) ON CONFLICT(Id) DO UPDATE SET Data=$data";
        cmd.Parameters.AddWithValue("$id", task.Id);
        cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(task, Json));
        cmd.ExecuteNonQuery();
    }

    public async Task<object> SnapshotAsync()
    {
        await _gate.WaitAsync();
        try { return new { tasks = _tasks.Values.Where(t => t.Status != "removed").OrderByDescending(t => t.CreatedAt).Select(t => JsonSerializer.SerializeToElement(t, Json)).ToArray(), settings = _settings, aria2Available = File.Exists(_aria), hlsAvailable = File.Exists(_ffmpeg), engineError = _engineError }; }
        finally { _gate.Release(); }
    }

    public async Task<string> CreateAsync(CreateDownloadRequest request, byte[]? torrent = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopping) throw new InvalidOperationException("下载服务正在停止。");
            if (_tasks.Values.Count(t => t.Status != "removed") >= 1000) throw new ArgumentException("下载任务数量已达上限。");
            var kind = torrent is not null ? "bt" : ValidateSource(request.Source, request.Kind);
            if (request.Priority is < -1 or > 1) throw new ArgumentException("任务优先级必须为 -1、0 或 1。");
            var parent = PathGuard.EnsureAllowed(request.Directory);
            if (!System.IO.Directory.Exists(parent)) throw new ArgumentException("下载目录不存在。");
            if (VirtualFileSystem.Current is { } vfs && PathGuard.IsSameOrChild(parent, vfs.MountPath)) throw new ArgumentException("不能下载到只读虚拟挂载目录。");
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name)) name = kind == "magnet" ? "Magnet" : torrent is not null ? "Torrent" : Path.GetFileName(new Uri(request.Source).AbsolutePath);
            if (string.IsNullOrEmpty(name)) name = "Download";
            if (name.Length > 160 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") throw new ArgumentException("下载名称无效。");
            if (kind == "hls" && !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) name += ".mp4";
            if (kind == "hls" ? !File.Exists(_ffmpeg) : !File.Exists(_aria)) throw new InvalidOperationException("下载引擎缺失，请运行 scripts/prepare-download-tools.ps1 后重新发布。");
            var task = new DownloadTaskRecord { Source = torrent is null ? request.Source : "", Kind = kind, Name = name, AutoIndex = request.AutoIndex, Priority = request.Priority, Proxy = NormalizeProxy(request.Proxy), Headers = ValidateHeaders(request.Headers, request.UserAgent) };
            task.SaveDir = PathGuard.EnsureAllowed(Path.Combine(parent, "MediaHub-" + task.Id));
            Incomplete[task.SaveDir] = 0;
            System.IO.Directory.CreateDirectory(task.SaveDir);
            if (torrent is not null)
            {
                task.TorrentPath = Path.Combine(_data, task.Id + ".torrent");
                await File.WriteAllBytesAsync(task.TorrentPath, torrent);
            }
            _tasks.Add(task.Id, task);
            Save(task); // Persist ownership before creating any external task.
            try { await StartTaskAsync(task); }
            catch { task.Status = "error"; task.Error = "启动下载失败，请检查引擎状态后重试。"; Save(task); throw; }
            return task.Id;
        }
        finally { _gate.Release(); }
    }

    private static string? NormalizeProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(uri.Host)) throw new ArgumentException("代理地址无效，仅支持 HTTP、HTTPS 或 SOCKS5。");
        return value;
    }
    private static Dictionary<string, string> ValidateHeaders(Dictionary<string, string>? headers, string? userAgent)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userAgent)) result["User-Agent"] = userAgent.Trim();
        if (headers is not null)
        {
            if (headers.Count > 32) throw new ArgumentException("自定义请求头最多 32 项。");
            foreach (var (key, value) in headers)
            {
                if (key.Length is 0 or > 128 || value.Length > 8192 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || value.Any(c => c is '\r' or '\n' or '\0')) throw new ArgumentException("请求头格式无效。");
                result[key] = value;
            }
        }
        if (result.Sum(x => x.Key.Length + x.Value.Length) > 32768) throw new ArgumentException("请求头总长度不能超过 32 KB。");
        return result;
    }

    internal static string ValidateSource(string source, string? kind)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 16384 || source.Any(char.IsControl)) throw new ArgumentException("下载链接无效。");
        if (source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return "magnet";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("仅支持 HTTP、HTTPS、FTP 或磁力链接。");
        if (kind is not (null or "auto" or "hls")) throw new ArgumentException("下载类型无效。");
        return kind == "hls" || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "direct";
    }

    public async Task ActAsync(string id, string action)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopping) throw new ArgumentException("下载服务正在停止。");
            if (!_tasks.TryGetValue(id, out var task) || task.Status == "removed") throw new ArgumentException("下载任务不存在。");
            switch (action)
            {
                case "pause":
                    if (task.Status is not ("downloading" or "queued" or "fetching_metadata" or "finalizing")) throw new ArgumentException("当前任务不能暂停。");
                    if (_hls.TryGetValue(id, out var run)) run.Cancellation.Cancel();
                    else if (task.Gid is not null && !task.WaitingForSlot) await CallAsync("aria2.forcePause", task.Gid);
                    task.WaitingForSlot = false;
                    task.Status = "paused"; task.DownloadSpeed = 0; task.ActiveConnections = 0; break;
                case "resume":
                    if (task.Status is not ("paused" or "error")) throw new ArgumentException("当前任务不能继续。");
                    if (_hls.ContainsKey(id)) throw new ArgumentException("任务正在暂停，请稍后继续。");
                    if (task.Status == "error" || _engine is not { HasExited: false }) task.Gid = null;
                    await StartTaskAsync(task);
                    break;
                case "remove":
                    if (_hls.TryGetValue(id, out var removedRun)) removedRun.Cancellation.Cancel();
                    if (task.Gid is not null && task.Status is not ("completed" or "error")) await CallAsync("aria2.forceRemove", task.Gid);
                    task.Status = "removed"; task.DownloadSpeed = 0; task.ActiveConnections = 0; break; // Never delete payload files.
                case "index":
                    if (task.Status != "completed") throw new ArgumentException("仅能索引已完成任务。");
                    Complete(task, true); break;
                default: throw new ArgumentException("下载操作无效。");
            }
            Save(task);
        }
        finally { _gate.Release(); }
    }

    public async Task BulkActAsync(string[] ids, string action)
    {
        if (ids.Length is < 1 or > 100) throw new ArgumentException("批量操作需选择 1–100 个任务。");
        foreach (var id in ids.Distinct(StringComparer.Ordinal)) await ActAsync(id, action);
    }
    public async Task SetPriorityAsync(string[] ids, int priority)
    {
        if (ids.Length is < 1 or > 100 || priority is < -1 or > 1) throw new ArgumentException("请选择 1–100 个任务，并设置 -1 至 1 的优先级。");
        await _gate.WaitAsync();
        try { foreach (var id in ids.Distinct()) { if (!_tasks.TryGetValue(id, out var task) || task.Status == "removed") continue; task.Priority = priority; Save(task); } }
        finally { _gate.Release(); }
    }

    public async Task<object> InspectAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_tasks.TryGetValue(id, out var task) || task.Status == "removed") throw new ArgumentException("下载任务不存在。");
            if (task.Gid is null) return TaskDetails(task);
            var state = await CallAsync("aria2.tellStatus", task.Gid, new[] { "status", "totalLength", "completedLength", "uploadLength", "downloadSpeed", "uploadSpeed", "connections", "numSeeders", "seeder", "bitfield", "bittorrent", "files", "errorCode", "errorMessage", "connections" });
            task.Details = JsonSerializer.Serialize(state, Json); task.DetailError = null; Save(task);
            return new { task = JsonSerializer.SerializeToElement(task, Json), engine = state };
        }
        catch (Exception ex) { if (_tasks.TryGetValue(id, out var task)) task.DetailError = ex.Message; throw; }
        finally { _gate.Release(); }
    }
    private static object TaskDetails(DownloadTaskRecord task) => new { task = JsonSerializer.SerializeToElement(task, Json), engine = task.Details is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(task.Details) };
    public async Task<DownloadTaskRecord[]> ExportAsync(string[]? ids = null)
    {
        await _gate.WaitAsync();
        try { var selected = ids is null ? null : ids.ToHashSet(StringComparer.Ordinal); return _tasks.Values.Where(t => t.Status != "removed" && (selected is null || selected.Contains(t.Id))).ToArray(); }
        finally { _gate.Release(); }
    }
    private string GetCompletedFileCore(string id, int index)
    {
        if (!_tasks.TryGetValue(id, out var task) || task.Status != "completed" || index < 0 || index >= task.Files.Length) throw new ArgumentException("下载文件不存在或尚未完成。");
        var path = PathGuard.EnsureAllowed(task.Files[index]);
        if (!PathGuard.IsSameOrChild(path, task.SaveDir) || !File.Exists(path)) throw new ArgumentException("下载文件不存在。");
        return path;
    }
    public async Task<string> CreateFileTicketAsync(string id, int index)
    {
        await _gate.WaitAsync();
        string path;
        try { path = GetCompletedFileCore(id, index); }
        finally { _gate.Release(); }
        foreach (var expired in _fileTickets.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToArray()) _fileTickets.TryRemove(expired, out _);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _fileTickets[ticket] = (path, DateTime.UtcNow.AddMinutes(2)); return ticket;
    }
    public string? ConsumeFileTicket(string ticket)
    {
        if (!_fileTickets.TryRemove(ticket, out var item) || item.Expires < DateTime.UtcNow) return null;
        try { var path = PathGuard.EnsureAllowed(item.Path); return File.Exists(path) ? path : null; } catch { return null; }
    }
    public async Task<string> GetTaskDirectoryAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (!_tasks.TryGetValue(id, out var task) || task.Status == "removed") throw new ArgumentException("下载任务不存在。"); var path = PathGuard.EnsureAllowed(task.SaveDir); if (!Directory.Exists(path)) throw new ArgumentException("下载目录不存在。"); return path; }
        finally { _gate.Release(); }
    }
    public async Task<string> GetFirstCompletedFileAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (!_tasks.TryGetValue(id, out var task) || task.Status != "completed") throw new ArgumentException("下载文件尚未完成。"); return GetCompletedFileCore(id, 0); }
        finally { _gate.Release(); }
    }
    public async Task ImportAsync(DownloadTaskRecord[] tasks)
    {
        if (tasks.Length is < 1 or > 100) throw new ArgumentException("导入任务数量需为 1–100。");
        foreach (var imported in tasks)
        {
            var request = new CreateDownloadRequest(imported.Source, imported.SaveDir, imported.Name, imported.AutoIndex, imported.Kind, imported.Proxy, imported.Headers, imported.Priority);
            if (string.IsNullOrWhiteSpace(imported.Source) || imported.Kind == "bt") throw new ArgumentException("导入 BT 任务需重新选择种子文件；当前仅支持导入有下载链接的任务。");
            var id = await CreateAsync(request);
            if (imported.Status == "paused") await ActAsync(id, "pause");
        }
    }

    private async Task EnsureEngineAsync()
    {
        if (_engine is { HasExited: false }) return;
        _engine?.Dispose();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); _rpcPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var config = Path.Combine(_data, "aria2.conf");
        await File.WriteAllTextAsync(config, $"enable-rpc=true\nrpc-listen-all=false\nrpc-listen-port={_rpcPort}\nrpc-secret={_secret}\ncontinue=true\nallow-overwrite=false\nauto-file-renaming=false\nmax-concurrent-downloads=8\nmax-connection-per-server=4\nmax-overall-download-limit={_settings.MaxDownloadSpeed}\nall-proxy={_settings.Proxy ?? ""}\nfile-allocation=none\nseed-time=0\nfollow-torrent=false\ncheck-certificate=true\nconsole-log-level=error\n");
        var psi = new ProcessStartInfo(_aria) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _data, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--conf-path=" + config);
        psi.ArgumentList.Add("--stop-with-process=" + Environment.ProcessId);
        _engine = Process.Start(psi) ?? throw new IOException("无法启动 aria2。");
        _job.Add(_engine);
        _ = DrainAsync(_engine.StandardOutput);
        _ = DrainAsync(_engine.StandardError);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (_engine.HasExited) break;
            try { await CallAsync("aria2.getVersion"); _engineError = null; return; }
            catch { await Task.Delay(150); }
        }
        _engineError = "aria2 启动失败。";
        throw new IOException(_engineError);
    }

    private async Task<JsonElement> CallAsync(string method, params object[] args)
    {
        // aria2's HTTP server requires Content-Length. JsonContent streams with
        // chunked transfer encoding, which causes its RPC parser to stall.
        using var content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = new object[] { "token:" + _secret }.Concat(args).ToArray() }), Encoding.UTF8, "application/json");
        using var response = await _rpc.PostAsync($"http://127.0.0.1:{_rpcPort}/jsonrpc", content);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("error", out _)) throw new IOException("下载引擎操作失败。");
        return doc.RootElement.GetProperty("result").Clone();
    }

    private async Task StartTaskAsync(DownloadTaskRecord task)
    {
        PathGuard.EnsureAllowed(task.SaveDir);
        if (ActiveTaskCount >= _settings.MaxConcurrentTasks || _hls.ContainsKey(task.Id))
        {
            task.Status = "queued"; task.WaitingForSlot = true; Save(task); return;
        }
        task.WaitingForSlot = false;
        task.Error = null; task.IndexError = null;
        if (task.Kind == "hls") { StartHls(task); return; }
        await EnsureEngineAsync();
        var options = ConnectionOptions(); options["dir"] = task.SaveDir;
        ApplyTaskOptions(options, task);
        task.ConnectionLimit = task.Kind == "direct" ? _settings.ConnectionsPerFile : 0;
        if (task.Gid is not null)
        {
            var resumedOptions = ConnectionOptions(); ApplyTaskOptions(resumedOptions, task); await CallAsync("aria2.changeOption", task.Gid, resumedOptions);
            await CallAsync("aria2.unpause", task.Gid); task.Status = "downloading"; Save(task); return;
        }
        if (task.Kind == "direct") options["out"] = task.Name;
        var result = task.TorrentPath is not null
            ? await CallAsync("aria2.addTorrent", Convert.ToBase64String(await File.ReadAllBytesAsync(task.TorrentPath)), Array.Empty<string>(), options)
            : await CallAsync("aria2.addUri", new[] { task.Source }, options);
        task.Gid = result.GetString(); task.Status = task.Kind == "magnet" ? "fetching_metadata" : "downloading";
        Save(task);
    }

    private Dictionary<string, string> ConnectionOptions() => new()
    {
        ["max-connection-per-server"] = _settings.ConnectionsPerFile.ToString(CultureInfo.InvariantCulture),
        ["split"] = _settings.ConnectionsPerFile.ToString(CultureInfo.InvariantCulture),
        ["min-split-size"] = "1M"
    };
    private static void ApplyTaskOptions(Dictionary<string, string> options, DownloadTaskRecord task)
    {
        if (task.Proxy is not null) options["all-proxy"] = task.Proxy;
        options["header"] = task.Headers.Select(h => h.Key + ": " + h.Value).ToArray() is { Length: > 0 } headers ? string.Join("\n", headers) : "";
    }

    private void StartHls(DownloadTaskRecord task)
    {
        var run = new HlsRun(); _hls[task.Id] = run;
        var threads = _settings.HlsThreads;
        task.Status = "downloading"; task.DownloadMode = "parallel-hls"; task.ConnectionLimit = threads;
        task.Progress = null; task.DownloadedBytes = 0; task.ActiveConnections = 0;
        task.CompletedSegments = task.TotalSegments = 0; _lastHlsBytes[task.Id] = 0;
        Save(task);
        run.Work = Task.Run(() => RunHlsAsync(task, run, threads));
    }

    private async Task RunHlsAsync(DownloadTaskRecord task, HlsRun run, int threads)
    {
        var token = run.Cancellation.Token;
        Process? process = null;
        string? local = null;
        var completed = false;
        try
        {
            local = await HlsParallelDownloader.PrepareAsync(task.Source, task.SaveDir, threads, task.Proxy ?? _settings.Proxy, task.Headers, async p =>
            {
                await _gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    task.DownloadedBytes = p.Bytes; task.CompletedSegments = p.Completed; task.TotalSegments = p.Total;
                    task.ActiveConnections = p.Active; task.Progress = Math.Min(.9999, (double)p.Completed / p.Total);
                }
                finally { _gate.Release(); }
            }, token);
            var output = PathGuard.EnsureAllowed(Path.Combine(task.SaveDir, task.Name + ".partial"));
            await _gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                task.DownloadMode = local is null ? "ffmpeg" : "parallel-hls";
                task.Status = local is null ? "downloading" : "finalizing";
                task.ActiveConnections = local is null ? 1 : 0;
                if (local is null) task.ConnectionLimit = 1;
                task.Progress = null; Save(task);
                var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", local is null ? "http,https,tcp,tls,crypto" : "file", "-rw_timeout", "15000000" }) psi.ArgumentList.Add(arg);
                if (local is null)
                {
                    psi.ArgumentList.Add("-user_agent"); psi.ArgumentList.Add(task.Headers.GetValueOrDefault("User-Agent", "Mozilla/5.0"));
                    var headers = task.Headers.Where(h => !h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key + ": " + h.Value + "\r\n").ToArray();
                    if (headers.Length > 0) { psi.ArgumentList.Add("-headers"); psi.ArgumentList.Add(string.Concat(headers)); }
                    var proxy = task.Proxy ?? _settings.Proxy;
                    if (!string.IsNullOrWhiteSpace(proxy)) { if (proxy.StartsWith("socks5:", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("FFmpeg HLS 兼容模式不支持 SOCKS5，请改用 HTTP/HTTPS 代理。"); psi.ArgumentList.Add("-http_proxy"); psi.ArgumentList.Add(proxy); }
                }
                foreach (var arg in new[] { "-i", local ?? task.Source, "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-f", "mp4", "-movflags", "+faststart", "-progress", "pipe:1", output }) psi.ArgumentList.Add(arg);
                process = Process.Start(psi) ?? throw new IOException("Unable to start media process");
                _job.Add(process); _media[task.Id] = process;
            }
            finally { _gate.Release(); }
            using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var errors = ReadDiagnosticAsync(process.StandardError);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (local is not null || !line.StartsWith("total_size=")) continue;
                await _gate.WaitAsync(token);
                try { if (long.TryParse(line[11..], out var size)) task.DownloadedBytes = size; }
                finally { _gate.Release(); }
            }
            await process.WaitForExitAsync(token); var diagnostic = await errors;
            await _gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (process.ExitCode == 0)
                {
                    var final = PathGuard.EnsureAllowed(Path.Combine(task.SaveDir, task.Name));
                    File.Move(PathGuard.EnsureAllowed(output), final, false);
                    task.Files = [final]; task.TotalBytes = task.DownloadedBytes = new FileInfo(final).Length;
                    Complete(task, task.AutoIndex);
                    completed = true;
                }
                else { task.Status = "error"; task.Error = HlsFailure(diagnostic); }
                Save(task);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await _gate.WaitAsync();
            try
            {
                if (!token.IsCancellationRequested && !_stopping)
                {
                    task.Status = "error";
                    task.Error = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden } ? HlsFailure("403 Forbidden")
                        : ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ? HlsFailure("401 Unauthorized")
                        : ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound } ? HlsFailure("404 Not Found")
                        : ex is TaskCanceledException ? HlsFailure("timed out") : HlsFailure("");
                    Save(task);
                }
            }
            finally { _gate.Release(); }
        }
        finally
        {
            if (process is not null) { try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); } catch { } }
            if (completed && local is not null) HlsParallelDownloader.CleanCompletedCache(local, task.SaveDir);
            await _gate.WaitAsync();
            try
            {
                _media.Remove(task.Id); _hls.Remove(task.Id); _lastHlsBytes.Remove(task.Id);
                task.ActiveConnections = 0; task.DownloadSpeed = 0; Save(task);
            }
            finally { _gate.Release(); }
            process?.Dispose(); run.Cancellation.Dispose();
        }
    }
    private static async Task DrainAsync(StreamReader reader) { var buffer = new char[2048]; while (await reader.ReadAsync(buffer) != 0) { } }
    private static async Task<string> ReadDiagnosticAsync(StreamReader reader)
    {
        var tail = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
        }
        return tail.ToString();
    }
    internal static string HlsFailure(string diagnostic)
    {
        if (diagnostic.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase)) return "服务器拒绝访问（HTTP 403）；链接可能需要 Referer、Cookie 或已过期。";
        if (diagnostic.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase)) return "服务器要求身份认证（HTTP 401）。";
        if (diagnostic.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase)) return "播放列表、密钥或分片不存在（HTTP 404）。";
        if (diagnostic.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "媒体服务器连接超时，请稍后重试。";
        return "HLS 下载或封装失败；请检查链接、密钥访问权限和编码兼容性。";
    }

    private void Complete(DownloadTaskRecord task, bool index)
    {
        var files = task.Files.Select(PathGuard.EnsureAllowed).ToArray();
        if (files.Length == 0 || files.Any(file => !PathGuard.IsSameOrChild(file, task.SaveDir) || !File.Exists(file))) throw new IOException("下载输出路径无效。");
        task.Files = files;
        task.Status = "completed"; task.Progress = 1; task.DownloadSpeed = 0; task.ActiveConnections = 0; task.WaitingForSlot = false;
        Incomplete.TryRemove(task.SaveDir, out _);
        if (!index || task.Indexed) return;
        try
        {
            foreach (var file in task.Files)
            {
                FileIndexer.Index(file, _db, false);
            }
            task.Indexed = true; task.IndexError = null;
        }
        catch { task.IndexError = "文件已下载，但索引失败；请重试索引。"; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previousTick = Stopwatch.GetTimestamp();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    var elapsed = Math.Max(.1, Stopwatch.GetElapsedTime(previousTick).TotalSeconds); previousTick = Stopwatch.GetTimestamp();
                    foreach (var task in _tasks.Values.Where(t => _hls.ContainsKey(t.Id)))
                    {
                        task.DownloadSpeed = task.Status == "downloading" ? Math.Max(0, (long)((task.DownloadedBytes - _lastHlsBytes.GetValueOrDefault(task.Id)) / elapsed)) : 0;
                        _lastHlsBytes[task.Id] = task.DownloadedBytes;
                    }
                    foreach (var task in _tasks.Values.Where(t => !t.WaitingForSlot && t.Gid is not null && t.Status is "downloading" or "queued" or "fetching_metadata").ToArray())
                    {
                        try
                        {
                            var state = await CallAsync("aria2.tellStatus", task.Gid!);
                            if (state.TryGetProperty("followedBy", out var next) && next.GetArrayLength() > 0) { task.Gid = next[0].GetString(); Save(task); continue; }
                            task.TotalBytes = Number(state, "totalLength"); task.DownloadedBytes = Number(state, "completedLength"); task.DownloadSpeed = Number(state, "downloadSpeed");
                            task.ActiveConnections = (int)Number(state, "connections");
                            task.Progress = task.TotalBytes > 0 ? Math.Min(.9999, (double)task.DownloadedBytes / task.TotalBytes) : null;
                            var status = state.GetProperty("status").GetString();
                            if (status == "complete")
                            {
                                task.Files = state.GetProperty("files").EnumerateArray().Where(f => !f.TryGetProperty("selected", out var s) || s.GetString() == "true").Select(f => f.GetProperty("path").GetString()!).ToArray();
                                Complete(task, task.AutoIndex);
                            }
                            else if (status == "error") { task.Status = "error"; task.Error = "下载失败（引擎代码 " + state.GetProperty("errorCode").GetString() + "）。"; task.DownloadSpeed = 0; }
                            else if (status == "paused") task.Status = "paused";
                            else task.Status = status == "waiting" ? "queued" : task.Kind == "magnet" && task.TotalBytes == 0 ? "fetching_metadata" : "downloading";
                            Save(task);
                        }
                        catch { task.Status = "error"; task.DownloadSpeed = 0; task.Error = "下载引擎连接中断，请重试任务。"; task.Gid = null; Save(task); }
                    }
                    if (_engine is { HasExited: false })
                    {
                        var limit = EffectiveSpeedLimit(_settings);
                        try { await CallAsync("aria2.changeGlobalOption", new Dictionary<string, string> { ["max-overall-download-limit"] = limit > 0 ? limit.ToString(CultureInfo.InvariantCulture) : "0", ["all-proxy"] = _settings.Proxy ?? "" }); } catch { }
                    }
                    foreach (var task in _tasks.Values.Where(t => t.Status == "queued" && t.WaitingForSlot).OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt).ToArray())
                    {
                        if (ActiveTaskCount >= _settings.MaxConcurrentTasks) break;
                        try { await StartTaskAsync(task); }
                        catch { task.Status = "error"; task.Error = "启动下载失败，请检查引擎状态后重试。"; Save(task); }
                    }
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private static long Number(JsonElement element, string name) => element.TryGetProperty(name, out var v) && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static long EffectiveSpeedLimit(DownloadSettings settings)
    {
        if (settings.ScheduleStart is null || !TimeOnly.TryParse(settings.ScheduleStart, out var start) || !TimeOnly.TryParse(settings.ScheduleEnd, out var end)) return settings.MaxDownloadSpeed;
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var active = start <= end ? now >= start && now < end : now >= start || now < end;
        return active ? settings.ScheduledSpeedLimit : settings.MaxDownloadSpeed;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] workers;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _stopping = true;
            foreach (var task in _tasks.Values.Where(t => t.Status is "downloading" or "queued" or "fetching_metadata" or "finalizing")) { task.Status = "paused"; task.DownloadSpeed = 0; task.ActiveConnections = 0; Save(task); }
            workers = _hls.Values.Select(r => r.Work!).Where(t => t is not null).ToArray();
            foreach (var run in _hls.Values) run.Cancellation.Cancel();
            foreach (var process in _media.Values) { try { if (!process.HasExited) process.Kill(true); } catch { } }
            if (_engine is { HasExited: false }) { try { await CallAsync("aria2.shutdown"); } catch { _engine.Kill(true); } }
        }
        finally { _gate.Release(); }
        try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _job.Dispose();
        _rpc.Dispose();
        _engine?.Dispose();
        base.Dispose();
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/downloads", async (DownloadService service) => Results.Ok(await service.SnapshotAsync()));
        app.MapGet("/api/downloads/settings", async (DownloadService service) => Results.Ok(await service.SettingsAsync()));
        app.MapPut("/api/downloads/settings", async (DownloadSettings settings, DownloadService service) =>
        {
            try { return Results.Ok(await service.SettingsAsync(settings)); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex is ArgumentException ? ex.Message : "并发设置保存失败。" }); }
        });
        app.MapPost("/api/downloads", async (CreateDownloadRequest request, DownloadService service) =>
        {
            try { return Results.Ok(new { id = await service.CreateAsync(request) }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex is ArgumentException ? ex.Message : "无法启动下载，请检查引擎、目录权限及服务状态。" }); }
        });
        app.MapPost("/api/downloads/bulk", async (BulkDownloadRequest request, DownloadService service) =>
        {
            try { if (request.Action == "priority") await service.SetPriorityAsync(request.Ids, request.Priority); else await service.BulkActAsync(request.Ids, request.Action); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex is ArgumentException ? ex.Message : "批量操作失败。" }); }
        });
        app.MapGet("/api/downloads/export", async (string[]? id, DownloadService service) => Results.Json(await service.ExportAsync(id)));
        app.MapPost("/api/downloads/import", async (DownloadTaskRecord[] tasks, DownloadService service) => { try { await service.ImportAsync(tasks); return Results.Ok(new { ok = true }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
        app.MapGet("/api/downloads/{id}/details", async (string id, DownloadService service) => { try { return Results.Ok(await service.InspectAsync(id)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
        app.MapPost("/api/downloads/{id}/file-ticket/{index:int}", async (string id, int index, DownloadService service) => { try { return Results.Ok(new { ticket = await service.CreateFileTicketAsync(id, index) }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
        app.MapGet("/api/downloads/file-ticket/{ticket}", (string ticket, DownloadService service) => { var path = service.ConsumeFileTicket(ticket); return path is null ? Results.NotFound() : Results.File(path, "application/octet-stream", Path.GetFileName(path)); });
        app.MapPost("/api/downloads/{id}/open-folder", async (string id, HttpContext context, DownloadService service) =>
        {
            if (!IsLocalClient(context.Connection.RemoteIpAddress)) return Results.BadRequest(new { error = "只有运行在服务器本机浏览器中的页面可以打开服务器目录；远程浏览器请复制路径。" });
            try { var path = await service.GetTaskDirectoryAsync(id); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/downloads/{id}/open-file", async (string id, HttpContext context, DownloadService service) =>
        {
            if (!IsLocalClient(context.Connection.RemoteIpAddress)) return Results.BadRequest(new { error = "只有服务器本机浏览器可以在服务器上打开文件。远程浏览器请使用“保存到本机”。" });
            try { var path = await service.GetFirstCompletedFileAsync(id); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/downloads/torrent", async (HttpRequest request, DownloadService service) =>
        {
            if (request.ContentLength is > 8 * 1024 * 1024) return Results.BadRequest(new { error = "种子文件最大为 8 MB。" });
            try
            {
                // Raw bytes avoid the application's 20 GB multipart upload limit.
                using var data = new MemoryStream(); var buffer = new byte[8192]; int read;
                while ((read = await request.Body.ReadAsync(buffer)) > 0) { if (data.Length + read > 8 * 1024 * 1024) throw new ArgumentException("种子文件最大为 8 MB。"); data.Write(buffer, 0, read); }
                if (data.Length == 0 || data.GetBuffer()[0] != (byte)'d') throw new ArgumentException("种子文件无效。");
                return Results.Ok(new { id = await service.CreateAsync(new("", request.Query["directory"].ToString(), request.Query["name"].ToString(), request.Query["autoIndex"] != "false"), data.ToArray()) });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex is ArgumentException ? ex.Message : "无法添加种子，请检查文件和引擎状态。" }); }
        });
        app.MapPost("/api/downloads/{id}/{action}", async (string id, string action, DownloadService service) =>
        {
            try { await service.ActAsync(id, action); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex is ArgumentException ? ex.Message : "下载操作失败，请刷新状态后重试。" }); }
        });
    }
}

record BulkDownloadRequest(string[] Ids, string Action, int Priority = 0);
