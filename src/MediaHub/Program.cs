using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

if (args.Length == 2 && args[0] == "--index")
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("X-MediaHub-Key", File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaHub", "api.key")).Trim());
    var servicePort = Environment.GetEnvironmentVariable("MEDIAHUB_PORT") ?? "8765";
    var response = await client.PostAsJsonAsync($"http://127.0.0.1:{servicePort}/api/index", new { path = args[1], recursive = true });
    if (!response.IsSuccessStatusCode) System.Windows.Forms.MessageBox.Show(await response.Content.ReadAsStringAsync(), "加入索引失败");
    return;
}
ExplorerIndexMenu.Register();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20L * 1024 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 20L * 1024 * 1024 * 1024);
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<MediaDb>();
builder.Services.AddSingleton<VirtualFileSystem>();
builder.Services.AddHostedService<VirtualFileSystemService>();
builder.Services.AddSingleton<WatchService>();
builder.Services.AddHostedService<WatchService>(sp => sp.GetRequiredService<WatchService>());

var app = builder.Build();
var paths = app.Services.GetRequiredService<AppPaths>();
var db = app.Services.GetRequiredService<MediaDb>();
db.Initialize();

var apiKey = paths.GetOrCreateApiKey();
var apiKeyLock = new object();
var port = int.TryParse(Environment.GetEnvironmentVariable("MEDIAHUB_PORT"), out var p) ? p : 8765;
DlnaService.Map(app, port);
SharePermissions.Map(app);
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") &&
        !ctx.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
    {
        var supplied = ctx.Request.Headers["X-MediaHub-Key"].ToString();
        var a = Encoding.UTF8.GetBytes(supplied);
        var b = Encoding.UTF8.GetBytes(Volatile.Read(ref apiKey));
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "未授权：请填写服务窗口显示的 API 密钥。" });
            return;
        }
    }
    await next();
});

app.MapGet("/api/health", () => Results.Ok(new { ok = true, app = "MediaHub", version = "0.1.0" }));

app.MapGet("/api/vfs/status", (VirtualFileSystem fs) => Results.Ok(new { mounted = fs.IsMounted, path = fs.MountPath, error = fs.Error, mode = "read-only" }));
app.MapPost("/api/security/api-key", (ApiKeyChangeRequest request) =>
{
    var next = string.IsNullOrWhiteSpace(request.NewKey) ? RandomNumberGenerator.GetInt32(1000000).ToString("D6") : request.NewKey.Trim();
    lock (apiKeyLock)
    {
        var file = Path.Combine(paths.DataDirectory, "api.key");
        File.WriteAllText(file + ".tmp", next, new UTF8Encoding(false));
        File.Move(file + ".tmp", file, true);
        Interlocked.Exchange(ref apiKey, next);
    }
    return Results.Ok(new { ok = true, apiKey = next });
});
app.MapGet("/api/config", () => Results.Ok(new
{
    app = "MediaHub NAS",
    version = "0.1.0",
    port,
    computerName = Environment.MachineName,
    currentAccount = WindowsIdentity.GetCurrent().Name,
    sqliteVersion = SQLitePCL.raw.sqlite3_libversion().utf8_to_string(),
    vfsMount = app.Services.GetRequiredService<VirtualFileSystem>().SharePath,
    dataDirectory = paths.DataDirectory,
    inbox = paths.InboxDirectory,
    isAdministrator = WindowsIdentity.GetCurrent().Owner is not null &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
    roots = PathGuard.GetRoots()
}));

app.MapGet("/api/items", (string? search, string? category) =>
    Results.Ok(new { items = db.SearchItems(search ?? "", category ?? ""), count = db.CountItems() }));

app.MapGet("/api/browse", (string? path) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = PathGuard.GetRoots().Select(x => new
            {
                name = x,
                path = x,
                isDirectory = true,
                isDrive = true,
                size = (long?)null,
                modified = (DateTime?)null
            });
            return Results.Ok(new { current = "", parent = "", entries = drives });
        }

        var full = PathGuard.EnsureAllowed(path);
        if (!Directory.Exists(full)) return Results.BadRequest(new { error = "目录不存在或无法访问。" });
        var parent = Directory.GetParent(full)?.FullName ?? "";
        var entries = new List<object>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(full).Take(5000))
        {
            try
            {
                var attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                var isDir = (attr & FileAttributes.Directory) != 0;
                var info = isDir ? (FileSystemInfo)new DirectoryInfo(entry) : new FileInfo(entry);
                var size = isDir ? (long?)null : ((FileInfo)info).Length;
                entries.Add(new
                {
                    name = info.Name,
                    path = info.FullName,
                    isDirectory = isDir,
                    isDrive = false,
                    size,
                    modified = info.LastWriteTime
                });
            }
            catch { /* skip inaccessible or transient entries */ }
        }
        return Results.Ok(new
        {
            current = full,
            parent,
            entries = entries.OrderByDescending(x => (bool)x.GetType().GetProperty("isDirectory")!.GetValue(x)!)
                             .ThenBy(x => (string)x.GetType().GetProperty("name")!.GetValue(x)!, StringComparer.OrdinalIgnoreCase)
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/index", async (IndexRequest request) =>
{
    try
    {
        var full = PathGuard.EnsureAllowed(request.Path);
        if (!File.Exists(full) && !Directory.Exists(full))
            return Results.NotFound(new { error = "原始文件或文件夹不存在。" });
        var count = FileIndexer.Index(full, db, request.Recursive);
        await SmbPublisher.PublishAsync(db);
        db.Log("index", full, $"Indexed {count} item(s)");
        return Results.Ok(new { ok = true, indexed = count, total = db.CountItems() });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/index/description", (DescriptionRequest request) =>
{
    if (request.Description is null || request.Description.Length > 4000) return Results.BadRequest(new { error = "描述最多 4000 字。" });
    return db.SetDescription(request.Id, request.Description) ? Results.Ok(new { ok = true }) : Results.NotFound();
});
app.MapPost("/api/index/display-name", (DisplayNameRequest request) =>
{
    if (request.Id <= 0 || request.Name is null || request.Name.Trim().Length > 255 || request.Name.Any(char.IsControl))
        return Results.BadRequest(new { error = "显示名称最多 255 字，留空恢复真实文件名。" });
    if (!db.SetDisplayName(request.Id, request.Name.Trim())) return Results.NotFound();
    VirtualFileSystem.Current?.Refresh(db);
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/index/file-name", async (DisplayNameRequest request) =>
{
    if (request.Id <= 0 || request.Name is null || request.Name.Trim().Length > 255 || request.Name.Any(char.IsControl))
        return Results.BadRequest(new { error = "显示名称最多 255 字，不能包含控制字符。留空可恢复原名称。" });
    try
    {
        var oldPath = db.RenameIndexedFile(request.Id, request.Name.Trim());
        if (oldPath is null) return Results.NotFound(new { error = "索引不存在，请刷新。" });
        await SmbPublisher.RemoveLinksAsync(oldPath);
        await SmbPublisher.PublishAsync(db);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/index/category", async (ChangeCategoryRequest request) =>
{
    if (request.Ids is null || request.Ids.Length is < 1 or > 10000 || request.Ids.Any(id => id <= 0) ||
        !new[] { "电影", "电视剧", "动漫", "照片", "视频", "音乐", "文档", "其他", "文件夹" }.Contains(request.Category))
        return Results.BadRequest(new { error = "请选择索引及有效分类。" });
    try
    {
        var changed = db.ChangeCategory(request.Ids.Distinct(), request.Category);
        await SmbPublisher.PublishAsync(db);
        return Results.Ok(new { ok = true, changed });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/index/batch-delete", async (BatchIndexDeleteRequest request) =>
{
    if (request.Ids is null || request.Ids.Length == 0 || request.Ids.Length > 10000 || request.Ids.Any(id => id <= 0))
        return Results.BadRequest(new { error = "请选择 1 至 10000 项索引。" });
    try
    {
        var removed = await SmbPublisher.RemoveIndexesAsync(db, request.Ids);
        db.Log("unindex-batch", "", $"Removed {removed} index records; originals unchanged");
        return Results.Ok(new { ok = true, removed });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/index", async ([FromBody] PathRequest request) =>
{
    try
    {
        var full = PathGuard.EnsureAllowed(request.Path);
        var removed = await SmbPublisher.RemoveIndexesAsync(db, path: full);
        db.Log("unindex", full, $"Removed {removed} index record(s); originals unchanged");
        return Results.Ok(new { ok = true, removed });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/files/upload", async (HttpRequest request, MediaDb database) =>
{
    try
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "需要 multipart/form-data。" });
        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "没有收到文件。" });
        var directoryValue = form["directory"].ToString();
        var directory = string.IsNullOrWhiteSpace(directoryValue) ? paths.InboxDirectory : PathGuard.EnsureAllowed(directoryValue);
        Directory.CreateDirectory(directory);
        var safeName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeName)) return Results.BadRequest(new { error = "文件名无效。" });
        var destination = PathGuard.UniquePath(Path.Combine(directory, safeName));
        await using (var output = File.Create(destination))
            await file.CopyToAsync(output);
        FileIndexer.Index(destination, database, false);
        database.Log("upload", destination, $"Uploaded {file.Length} bytes");
        return Results.Ok(new { ok = true, path = destination, name = Path.GetFileName(destination), size = file.Length });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/files/download", (string path) =>
{
    try
    {
        var full = PathGuard.EnsureAllowed(path);
        if (!File.Exists(full)) return Results.NotFound(new { error = "文件不存在。" });
        return Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/files/folder", (CreateFolderRequest request) =>
{
    try
    {
        var parent = PathGuard.EnsureAllowed(request.Parent);
        if (!Directory.Exists(parent)) return Results.BadRequest(new { error = "父目录不存在。" });
        var name = Path.GetFileName(request.Name.Trim());
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Results.BadRequest(new { error = "文件夹名称无效。" });
        var destination = Path.Combine(parent, name);
        if (File.Exists(destination) || Directory.Exists(destination))
            return Results.Conflict(new { error = "同名文件或文件夹已存在。" });
        Directory.CreateDirectory(destination);
        FileIndexer.Index(destination, db, false);
        db.Log("mkdir", destination, "Created directory");
        return Results.Ok(new { ok = true, path = destination });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/files/move", (MoveRequest request) =>
{
    try
    {
        var source = PathGuard.EnsureAllowed(request.Path);
        var targetDir = PathGuard.EnsureAllowed(request.DestinationDirectory);
        if (!File.Exists(source) && !Directory.Exists(source)) return Results.NotFound(new { error = "源文件不存在。" });
        if (!Directory.Exists(targetDir)) return Results.BadRequest(new { error = "目标目录不存在。" });
        if (Directory.Exists(source) && PathGuard.IsSameOrChild(targetDir, source))
            return Results.BadRequest(new { error = "不能将文件夹移动到自身内部。" });
        var destination = PathGuard.UniquePath(Path.Combine(targetDir, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))));
        FileOps.MovePath(source, destination);
        db.RemovePathAndChildren(source);
        FileIndexer.Index(destination, db, true);
        db.Log("move", destination, $"Moved from {source}");
        return Results.Ok(new { ok = true, path = destination });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/files/copy", (MoveRequest request) =>
{
    try
    {
        var source = PathGuard.EnsureAllowed(request.Path);
        var targetDir = PathGuard.EnsureAllowed(request.DestinationDirectory);
        if (!File.Exists(source) && !Directory.Exists(source)) return Results.NotFound(new { error = "源文件不存在。" });
        if (!Directory.Exists(targetDir)) return Results.BadRequest(new { error = "目标目录不存在。" });
        var destination = PathGuard.UniquePath(Path.Combine(targetDir, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))));
        if (Directory.Exists(source)) FileOps.CopyDirectory(source, destination);
        else File.Copy(source, destination, false);
        FileIndexer.Index(destination, db, true);
        db.Log("copy", destination, $"Copied from {source}");
        return Results.Ok(new { ok = true, path = destination });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/files", ([FromBody] PathRequest request) =>
{
    try
    {
        var full = PathGuard.EnsureAllowed(request.Path);
        PathGuard.EnsureNotDriveRoot(full);
        if (!File.Exists(full) && !Directory.Exists(full)) return Results.NotFound(new { error = "文件不存在。" });
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (Directory.Exists(full))
            FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        var removed = db.RemovePathAndChildren(full);
        db.Log("recycle", full, $"Sent {name} to Windows Recycle Bin; removed {removed} index record(s)");
        return Results.Ok(new { ok = true, recycled = true, removed });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = "移入回收站失败：" + ex.Message }); }
});

app.MapGet("/api/logs", (int? take) => Results.Ok(new { logs = db.GetLogs(Math.Clamp(take ?? 100, 1, 500)) }));

app.MapGet("/api/automation/watches", () => Results.Ok(new { watches = db.GetWatches() }));
app.MapPost("/api/automation/watches/sync", async () =>
{
    try
    {
        foreach (var watch in db.GetWatches()) FileIndexer.Index(watch.Path, db, true);
        await SmbPublisher.PublishAsync(db);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/automation/watches", (WatchRequest request, WatchService watcher) =>
{
    try
    {
        var full = PathGuard.EnsureAllowed(request.Path);
        if (!Directory.Exists(full)) return Results.BadRequest(new { error = "监控路径必须是已存在的文件夹。" });
        var watch = db.AddWatch(full);
        watcher.Add(full);
        db.Log("watch-add", full, "Folder watch enabled; waiting for file changes or manual sync");
        return Results.Ok(new { ok = true, watch });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/automation/watches/{id:int}", (int id, WatchService watcher) =>
{
    var watch = db.GetWatch(id);
    if (watch is null) return Results.NotFound(new { error = "监控任务不存在。" });
    db.RemoveWatch(id);
    watcher.Remove(watch.Path);
    db.Log("watch-remove", watch.Path, "Folder watch disabled");
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/automation/startup", async (StartupRequest request) =>
{
    try
    {
        await StartupService.SetEnabled(request.Enabled);
        db.Log("startup", "Task Scheduler", request.Enabled ? "Enabled" : "Disabled");
        return Results.Ok(new { ok = true, enabled = request.Enabled });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/smb/shares", async () =>
{
    try
    {
        var json = await PowerShell.RunAsync("$ErrorActionPreference='Stop'; @(Get-SmbShare | Where-Object {$_.Special -eq $false -and ($_.Description -eq 'MediaHub managed share' -or $_.Name -eq 'mediahub')} | Select-Object @{n='name';e={$_.Name}},@{n='path';e={$_.Path}},@{n='description';e={$_.Description}}) | ConvertTo-Json -Depth 4 -Compress");
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        var shares = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray()
            : new[] { doc.RootElement.Clone() };
        return Results.Ok(new { shares });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = "读取 SMB 共享失败（请确认 Windows 文件共享功能可用）：" + ex.Message }); }
});

var shareMutation = new SemaphoreSlim(1);
app.MapPost("/api/smb/shares", async (SmbCreateRequest request) =>
{
    await shareMutation.WaitAsync();
    try
    {
        if (!AdminCheck.IsAdministrator())
            return Results.Conflict(new { error = "创建 SMB 共享需要管理员权限。请关闭 MediaHub，右键 EXE 选择“以管理员身份运行”后再操作。" });
        var name = string.IsNullOrWhiteSpace(request.Name) ? "mediahub" : request.Name.Trim();
        var account = WindowsIdentity.GetCurrent().Name;
        var fs = app.Services.GetRequiredService<VirtualFileSystem>();
        if (!fs.IsMounted) return Results.Conflict(new { error = "WinFsp 尚未成功挂载，请检查 /api/vfs/status。" });
        var full = fs.SharePath;

        if (!Directory.Exists(full)) return Results.BadRequest(new { error = "共享路径必须是已存在的文件夹。" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_-]{1,80}$"))
            return Results.BadRequest(new { error = "共享名称仅支持英文、数字、下划线和短横线。" });
        if (string.IsNullOrWhiteSpace(account) || account.Contains('\n') || account.Contains('\r'))
            return Results.BadRequest(new { error = "请填写已存在的 Windows 用户，例如 COMPUTER\\media。" });
        var access = "ReadAccess";
        var script = "$ErrorActionPreference='Stop'; " +
                     $"$n={PowerShell.Quote(name)}; $p={PowerShell.Quote(full)}; $a={PowerShell.Quote(account)}; " +
                     "if(Get-SmbShare | Where-Object {$_.Description -eq 'MediaHub managed share' -or $_.Name -eq 'mediahub'}){throw '只能创建一个 MediaHub 共享，请先撤销已有共享。'}; " +
                     $"$old=Get-SmbShare -Name $n -ErrorAction SilentlyContinue; if($old){{throw '共享名称已存在，请先在界面删除旧共享。'}}; " +
                     $"New-SmbShare -Name $n -Path $p -Description 'MediaHub managed share' -{access} $a -ErrorAction Stop | Out-Null; " +
                     "'SMB_SHARE_CREATED'";
        await PowerShell.RunAsync(script);
        await SharePermissions.ApplySmb(SharePermissions.Load());
        SmbPublisher.Register(full);
        await SmbPublisher.PublishAsync(db);
        db.Log("smb-create", full, $"Share={name}; Account={account}; Access={request.Access}");
        return Results.Ok(new { ok = true, name, path = full, unc = $@"\\{Environment.MachineName}\{name}", account, access = request.Access,
            note = "共享权限已设置；实际访问还受该目录现有 NTFS 权限约束。" });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    finally { shareMutation.Release(); }
});

app.MapDelete("/api/smb/shares/{name}", async (string name) =>
{
    try
    {
        if (!AdminCheck.IsAdministrator())
            return Results.Conflict(new { error = "删除 SMB 共享需要管理员权限，请以管理员身份运行 MediaHub。" });
        await PowerShell.RunAsync($"$ErrorActionPreference='Stop'; Remove-SmbShare -Name {PowerShell.Quote(name)} -Force -Confirm:$false");
        db.Log("smb-delete", name, "SMB share removed; original files unchanged");
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

Console.WriteLine("==================================================");
Console.WriteLine(" MediaHub NAS 0.1.0");
Console.WriteLine($" 本机地址: http://127.0.0.1:{port}/");
Console.WriteLine($" 局域网地址: http://{GetLanAddress()}:{port}/");
Console.WriteLine($" API Key: {apiKey}");
Console.WriteLine($" 数据库: {paths.DatabasePath}");
Console.WriteLine(" 仅在可信家庭局域网使用；不要将端口转发到公网。");
Console.WriteLine(" 按 Ctrl+C 停止服务。");
Console.WriteLine("==================================================");
await app.StartAsync();
var firstPageMarker = Path.Combine(paths.DataDirectory, "first-page-opened");
if (!File.Exists(firstPageMarker) || !args.Contains("--background", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/#apiKey={Uri.EscapeDataString(Volatile.Read(ref apiKey))}") { UseShellExecute = true });
        File.WriteAllText(firstPageMarker, "opened");
    }
    catch { }
}
using var tray = new TrayHost(app.Lifetime, () => $"http://127.0.0.1:{port}/#apiKey={Uri.EscapeDataString(Volatile.Read(ref apiKey))}");
await app.WaitForShutdownAsync();

static string GetLanAddress()
{
    try
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        socket.Connect("192.0.2.1", 80);
        return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Address.ToString();
    }
    catch { return "服务器局域网 IP"; }
}

record IndexRequest(string Path, bool Recursive = true);
record PathRequest(string Path);
record BatchIndexDeleteRequest(long[] Ids);
record ChangeCategoryRequest(long[] Ids, string Category);
record DisplayNameRequest(long Id, string Name);
record DescriptionRequest(long Id, string Description);
record RenameRequest(string Path, string NewName);
record CreateFolderRequest(string Parent, string Name);
record MoveRequest(string Path, string DestinationDirectory);
record WatchRequest(string Path);
record StartupRequest(bool Enabled);
record ApiKeyChangeRequest(string? NewKey);
record SmbCreateRequest(string Name, string Path, string Account, string Access = "Change");

sealed class AppPaths
{
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaHub");
    public string DatabasePath => Path.Combine(DataDirectory, "mediahub.db");
    public string InboxDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MediaHub Inbox");
    public AppPaths() { Directory.CreateDirectory(DataDirectory); Directory.CreateDirectory(InboxDirectory); }
    public string GetOrCreateApiKey()
    {
        var file = Path.Combine(DataDirectory, "api.key");
        if (File.Exists(file)) return File.ReadAllText(file).Trim();
        var key = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
        File.WriteAllText(file, key, Encoding.ASCII);
        return key;
    }
}

static class PathGuard
{
    public static string[] GetRoots() => DriveInfo.GetDrives().Where(d => d.IsReady)
        .Select(d => Path.GetFullPath(d.RootDirectory.FullName)).ToArray();

    public static string EnsureAllowed(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("路径不能为空。");
        var full = Path.GetFullPath(input);
        var root = GetRoots().FirstOrDefault(r => IsUnder(full, r));
        if (root is null)
            throw new UnauthorizedAccessException("该路径不在当前服务器的本地磁盘范围内。");

        // Do not follow junctions/symlinks/reparse points outside the selected real path.
        var relative = Path.GetRelativePath(root, full);
        var current = root;
        if (relative != ".")
        {
            foreach (var part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new UnauthorizedAccessException("为避免越权访问，MediaHub 暂不允许通过符号链接或目录联接操作文件。");
                }
                else break;
            }
        }
        return full;
    }

    public static void EnsureNotDriveRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("为防止误操作，不允许对整个磁盘根目录执行此操作。");
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameOrChild(string candidate, string parent)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.Equals(parent, StringComparison.OrdinalIgnoreCase) || c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}

static class FileIndexer
{
    public static int Index(string path, MediaDb db, bool recursive)
    {
        var count = 0;
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) { db.Upsert(full); return 1; }
        if (!Directory.Exists(full)) throw new FileNotFoundException("路径不存在。", full);
        db.Upsert(full); count++;
        if (!recursive) return count;
        var stack = new Stack<string>();
        stack.Push(full);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(dir).ToArray(); }
            catch { continue; }
            foreach (var child in children)
            {
                try
                {
                    var attr = File.GetAttributes(child);
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    db.Upsert(child); count++;
                    if ((attr & FileAttributes.Directory) != 0) stack.Push(child);
                }
                catch { /* ignore inaccessible entries */ }
            }
        }
        return count;
    }

    public static string CategoryFor(string path) => MediaFormats.CategoryFor(path);
}

static class FileOps
{
    public static void MovePath(string source, string destination)
    {
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
            return;
        }

        // Cross-volume move: copy first, then send the source to Recycle Bin.
        if (Directory.Exists(source)) CopyDirectory(source, destination);
        else File.Copy(source, destination, false);

        try
        {
            if (Directory.Exists(source))
                FileSystem.DeleteDirectory(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            throw new IOException("目标副本已创建，但原文件未能移入回收站；原文件仍保留，请检查目标目录后手动处理。", ex);
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}

sealed class MediaDb
{
    private readonly string _connection;
    public MediaDb(AppPaths paths) => _connection = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }
    public void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS Items(
            Id INTEGER PRIMARY KEY AUTOINCREMENT, Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            Name TEXT NOT NULL, Category TEXT NOT NULL, IsDirectory INTEGER NOT NULL,
            Size INTEGER NOT NULL DEFAULT 0, ModifiedUtc TEXT NOT NULL, IndexedUtc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_Items_Category ON Items(Category);
        CREATE INDEX IF NOT EXISTS IX_Items_Name ON Items(Name);
        CREATE TABLE IF NOT EXISTS CategoryOverrides(Path TEXT PRIMARY KEY COLLATE NOCASE, Category TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS DisplayNames(Path TEXT PRIMARY KEY COLLATE NOCASE, Name TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Descriptions(Path TEXT PRIMARY KEY COLLATE NOCASE, Description TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Watches(Id INTEGER PRIMARY KEY AUTOINCREMENT, Path TEXT NOT NULL UNIQUE COLLATE NOCASE, CreatedUtc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Logs(Id INTEGER PRIMARY KEY AUTOINCREMENT, Action TEXT NOT NULL, Path TEXT NOT NULL, Detail TEXT NOT NULL, CreatedUtc TEXT NOT NULL);
        """;
        cmd.ExecuteNonQuery();
    }
    public void Upsert(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var isDir = Directory.Exists(path);
        var info = isDir ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) return;
        var size = isDir ? 0L : ((FileInfo)info).Length;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
        INSERT INTO Items(Path,Name,Category,IsDirectory,Size,ModifiedUtc,IndexedUtc)
        VALUES($p,$n,$cat,$d,$s,$m,$i)
        ON CONFLICT(Path) DO UPDATE SET Name=$n,Category=$cat,IsDirectory=$d,Size=$s,ModifiedUtc=$m,IndexedUtc=$i
        WHERE Items.Size<>$s OR Items.ModifiedUtc<>$m OR Items.IsDirectory<>$d OR Items.Category<>$cat;
        """;
        using var nameCommand = c.CreateCommand();
        nameCommand.CommandText = "SELECT Name FROM DisplayNames WHERE Path=$p";
        nameCommand.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$n", nameCommand.ExecuteScalar() as string ?? info.Name);
        using var categoryCommand = c.CreateCommand();
        categoryCommand.CommandText = "SELECT Category FROM CategoryOverrides WHERE Path=$p";
        categoryCommand.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$cat", categoryCommand.ExecuteScalar() as string ?? FileIndexer.CategoryFor(path)); cmd.Parameters.AddWithValue("$d", isDir ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", size); cmd.Parameters.AddWithValue("$m", info.LastWriteTimeUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$i", DateTime.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() > 0) SmbPublisher.RequestPublish();
    }
    public List<object> SearchItems(string search, string category, bool all = false)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Path,Name,Category,IsDirectory,Size,ModifiedUtc,IndexedUtc FROM Items WHERE ($q='' OR Name LIKE $like OR Path LIKE $like) AND ($cat='' OR Category=$cat) ORDER BY IsDirectory DESC, Name COLLATE NOCASE" + (all ? ";" : " LIMIT 10000;");
        cmd.Parameters.AddWithValue("$q", search); cmd.Parameters.AddWithValue("$like", "%" + search + "%"); cmd.Parameters.AddWithValue("$cat", category);
        using var r = cmd.ExecuteReader(); var list = new List<object>();
        while (r.Read())
        {
            var path = r.GetString(1);
            list.Add(new { id = r.GetInt64(0), path, name = r.GetString(2), category = r.GetString(3),
                isDirectory = r.GetInt32(4) == 1, size = r.GetInt64(5), modifiedUtc = r.GetString(6),
                indexedUtc = r.GetString(7), displayName = GetDisplayName(path), description = GetDescription(path), exists = File.Exists(path) || Directory.Exists(path) });
        }
        return list;
    }
    public int CountItems()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM Items"; return Convert.ToInt32(cmd.ExecuteScalar());
    }
    public int RemovePathAndChildren(string path)
    {
        var prefix = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Items WHERE Path=$p OR substr(Path,1,length($prefix))=$prefix;";
        cmd.Parameters.AddWithValue("$p", Path.GetFullPath(path)); cmd.Parameters.AddWithValue("$prefix", prefix);
        return cmd.ExecuteNonQuery();
    }
    string GetDisplayName(string path)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Name FROM DisplayNames WHERE Path=$p"; cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteScalar() as string ?? "";
    }
    string GetDescription(string path)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Description FROM Descriptions WHERE Path=$p"; cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteScalar() as string ?? "";
    }
    public bool SetDescription(long id, string description)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Descriptions(Path,Description) SELECT Path,$d FROM Items WHERE Id=$id ON CONFLICT(Path) DO UPDATE SET Description=$d";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$d", description);
        return cmd.ExecuteNonQuery() > 0;
    }
    public string? RenameIndexedFile(long id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("请输入有效文件名（包含扩展名）。");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Path FROM Items WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not string oldPath) return null;
        oldPath = PathGuard.EnsureAllowed(oldPath); PathGuard.EnsureNotDriveRoot(oldPath);
        if (Directory.Exists(oldPath)) throw new IOException("当前仅支持文件改名，文件夹暂不支持。");
        var destination = Path.Combine(Path.GetDirectoryName(oldPath)!, name);
        if (oldPath == destination) return oldPath;
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标名称已存在。");
        File.Move(oldPath, destination);
        try
        {
            using var transaction = c.BeginTransaction(); cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue("$p", oldPath); cmd.Parameters.AddWithValue("$new", destination); cmd.Parameters.AddWithValue("$n", name);
            cmd.CommandText = "UPDATE Items SET Path=$new,Name=COALESCE((SELECT Name FROM DisplayNames WHERE Path=$p),$n) WHERE Id=$id"; cmd.ExecuteNonQuery();
            foreach (var table in new[] { "CategoryOverrides", "Descriptions", "DisplayNames" }) { cmd.CommandText = $"UPDATE {table} SET Path=$new WHERE Path=$p"; cmd.ExecuteNonQuery(); }
            transaction.Commit();
        }
        catch { File.Move(destination, oldPath); throw; }
        return oldPath;
    }
    public bool SetDisplayName(long id, string name)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT Path FROM Items WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not string path) return false;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$n", name.Length == 0 ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : name);
        cmd.CommandText = name.Length == 0 ? "DELETE FROM DisplayNames WHERE Path=$p" :
            "INSERT INTO DisplayNames(Path,Name) VALUES($p,$n) ON CONFLICT(Path) DO UPDATE SET Name=$n";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "UPDATE Items SET Name=$n WHERE Id=$id";
        cmd.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }
    public int ChangeCategory(IEnumerable<long> ids, string category)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        var changed = 0;
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO CategoryOverrides(Path,Category) SELECT Path,$cat FROM Items WHERE Id=$id ON CONFLICT(Path) DO UPDATE SET Category=$cat;";
            cmd.Parameters.AddWithValue("$cat", category); cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "UPDATE Items SET Category=$cat WHERE Id=$id;";
            changed += cmd.ExecuteNonQuery();
        }
        transaction.Commit();
        SmbPublisher.RequestPublish();
        return changed;
    }
    public int RemoveIndexIds(IEnumerable<long> ids)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        var removed = 0;
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM Items WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            removed += cmd.ExecuteNonQuery();
        }
        transaction.Commit();
        return removed;
    }
    public List<string> GetIndexPathsForRemoval(long[]? ids, string? path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Path FROM Items";
        using var reader = cmd.ExecuteReader();
        var selectedIds = ids?.ToHashSet();
        var prefix = path?.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var paths = new List<string>();
        while (reader.Read())
        {
            var candidate = reader.GetString(1);
            if (selectedIds is not null ? selectedIds.Contains(reader.GetInt64(0)) :
                candidate == path || candidate.StartsWith(prefix!, StringComparison.Ordinal)) paths.Add(candidate);
        }
        return paths;
    }
    public object AddWatch(string path)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Watches(Path,CreatedUtc) VALUES($p,$d);";
        cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        return GetWatches().First(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
    }
    public List<WatchRecord> GetWatches()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,Path,CreatedUtc FROM Watches ORDER BY Id";
        using var r = cmd.ExecuteReader(); var list = new List<WatchRecord>();
        while (r.Read()) list.Add(new WatchRecord(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        return list;
    }
    public WatchRecord? GetWatch(int id) => GetWatches().FirstOrDefault(x => x.Id == id);
    public void RemoveWatch(int id) { using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="DELETE FROM Watches WHERE Id=$id"; cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery(); }
    public void Log(string action, string path, string detail)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="INSERT INTO Logs(Action,Path,Detail,CreatedUtc) VALUES($a,$p,$d,$t)";
        cmd.Parameters.AddWithValue("$a",action); cmd.Parameters.AddWithValue("$p",path); cmd.Parameters.AddWithValue("$d",detail); cmd.Parameters.AddWithValue("$t",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public List<object> GetLogs(int take)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT Id,Action,Path,Detail,CreatedUtc FROM Logs ORDER BY Id DESC LIMIT $n"; cmd.Parameters.AddWithValue("$n",take);
        using var r=cmd.ExecuteReader(); var list=new List<object>(); while(r.Read()) list.Add(new { id=r.GetInt64(0), action=r.GetString(1), path=r.GetString(2), detail=r.GetString(3), createdUtc=r.GetString(4) }); return list;
    }
}

record WatchRecord(int Id, string Path, string CreatedUtc);

sealed class WatchService : BackgroundService
{
    private readonly MediaDb _db;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    public WatchService(MediaDb db) => _db = db;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var w in _db.GetWatches()) Add(w.Path);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                if (!SmbPublisher.TakePending()) continue;
                try { await SmbPublisher.PublishAsync(_db); }
                catch (Exception ex) { _db.Log("publish-error", "", ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Add(string path)
    {
        if (_watchers.ContainsKey(path) || !Directory.Exists(path)) return;
        try
        {
            var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (_, e) => { try { if (File.Exists(e.FullPath) || Directory.Exists(e.FullPath)) _db.Upsert(e.FullPath); } catch { } };
            RenamedEventHandler renamed = (_, e) => { try { _db.RemovePathAndChildren(e.OldFullPath); if (File.Exists(e.FullPath) || Directory.Exists(e.FullPath)) _db.Upsert(e.FullPath); } catch { } };
            watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += (_, e) => { try { _db.RemovePathAndChildren(e.FullPath); } catch { } };
            watcher.Renamed += renamed; watcher.EnableRaisingEvents = true;
            if (!_watchers.TryAdd(path, watcher)) watcher.Dispose();
        }
        catch { }
    }
    public void Remove(string path) { if (_watchers.TryRemove(path, out var w)) { w.EnableRaisingEvents = false; w.Dispose(); } }
    public override void Dispose() { foreach (var w in _watchers.Values) w.Dispose(); base.Dispose(); }
}

static class AdminCheck
{
    public static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

static class PowerShell
{
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    public static async Task<string> RunAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PowerShell。");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        return stdout.Trim();
    }
}
