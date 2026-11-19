using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class SmbPublisher
{
    static readonly string[] Categories = ["电影", "电视剧", "动漫", "照片", "视频", "音乐", "文档", "其他", "文件夹"];
    static void CleanLinks(string root, string source, string? keepCategory = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..12];
        var name = Path.GetFileNameWithoutExtension(source) + "_" + hash + Path.GetExtension(source);
        foreach (var categoryRoot in new[] { root, Path.Combine(root, "分类索引") })
        foreach (var category in Categories)
        {
            if (categoryRoot == root && category == keepCategory) continue;
            var parent = PathGuard.EnsureAllowed(Path.Combine(categoryRoot, category));
            var link = new FileInfo(Path.Combine(parent, name));
            if (link.LinkTarget is not null && string.Equals(Path.GetFullPath(link.LinkTarget, parent), source, StringComparison.OrdinalIgnoreCase)) link.Delete();
        }
    }
    static readonly SemaphoreSlim Gate = new(1);
    static int pending = 1;
    public static void RequestPublish() => Interlocked.Exchange(ref pending, 1);
    public static bool TakePending() => Interlocked.Exchange(ref pending, 0) != 0;
    static string Settings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaHub", "publish-roots.json");
    static List<string> Roots() => File.Exists(Settings) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Settings)) ?? [] : [];
    public static void Register(string root)
    {
        File.WriteAllText(Settings, JsonSerializer.Serialize(new[] { root }));
    }
    public static async Task RemoveLinksAsync(string source)
    {
        await Gate.WaitAsync();
        try { foreach (var root in Roots()) CleanLinks(root, source); }
        finally { Gate.Release(); }
    }
    public static async Task<int> RemoveIndexesAsync(MediaDb db, long[]? ids = null, string? path = null)
    {
        await Gate.WaitAsync();
        try
        {
            var sources = db.GetIndexPathsForRemoval(ids, path);
            foreach (var root in Roots())
            foreach (var source in sources)
            {
                CleanLinks(root, source);
            }
            return ids is not null ? db.RemoveIndexIds(ids.Distinct()) : db.RemovePathAndChildren(path!);
        }
        finally { Gate.Release(); }
    }
    public static async Task PublishAsync(MediaDb db)
    {
        await Gate.WaitAsync();
        try
        {
            var existingRoot = await PowerShell.RunAsync("$s=Get-SmbShare -Name 'mediahub' -ErrorAction SilentlyContinue; if($s){$s.Path}");
            if (!string.IsNullOrWhiteSpace(existingRoot)) Register(PathGuard.EnsureAllowed(existingRoot.Trim()));
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(db.SearchItems("", "")));
            foreach (var root in Roots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.GetProperty("isDirectory").GetBoolean()) continue;
                    var sourcePath = item.GetProperty("path").GetString()!;
                    if (!File.Exists(sourcePath) || (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0) continue;
                    var source = PathGuard.EnsureAllowed(sourcePath);
                    var category = item.GetProperty("category").GetString()!;
                    var directory = PathGuard.EnsureAllowed(Path.Combine(root, category));
                    Directory.CreateDirectory(directory);
                    if (string.Equals(Path.GetDirectoryName(source), directory, StringComparison.OrdinalIgnoreCase)) continue;
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..12];
                    var target = Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + "_" + hash + Path.GetExtension(source));
                    var existing = new FileInfo(target);
                    if (existing.LinkTarget is not null)
                    {
                        if (!string.Equals(existing.LinkTarget, source, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"符号链接目标冲突，未覆盖：{target}");
                        CleanLinks(root, source, category);
                        continue;
                    }
                    if (File.Exists(target) || Directory.Exists(target))
                        throw new IOException($"分类入口已存在真实文件，未覆盖：{target}");
                    File.CreateSymbolicLink(target, source);
                    CleanLinks(root, source, category);
                }
            }
        }
        finally { Gate.Release(); }
    }
}
