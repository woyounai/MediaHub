using Fsp;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using VFileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

sealed class VirtualFileSystem : FileSystemBase, IDisposable
{
    sealed record Node(string VirtualPath, string? Source, VFileInfo Info);
    static readonly string[] Categories = ["电影", "电视剧", "动漫", "照片", "视频", "音乐", "文档", "其他", "文件夹"];
    Dictionary<string, Node> nodes = new(StringComparer.OrdinalIgnoreCase);
    byte[] security = [];
    FileSystemHost? host;
    public string MountPath { get; } = Environment.GetEnvironmentVariable("MEDIAHUB_VFS_MOUNT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaHub", "Share");
    public string SharePath => Path.Combine(MountPath, "mediahub");
    public bool IsMounted => host is not null;
    public string? Error { get; private set; }
    public static VirtualFileSystem? Current { get; private set; }
    public VirtualFileSystem()
    {
        ReloadPermissions();
        Current = this;
    }
    public void ReloadPermissions()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var users = SharePermissions.Load();
        var acl = $"O:{sid}G:SYD:P";
        foreach (var user in users.Where(u => u.Access == "None" && u.Sid != sid)) acl += $"(D;;FRFX;;;{user.Sid})";
        acl += $"(A;;FA;;;SY)(A;;FRFX;;;{sid})";
        foreach (var user in users.Where(u => u.Access == "Read" && u.Sid != sid)) acl += $"(A;;FRFX;;;{user.Sid})";
        var descriptor = new RawSecurityDescriptor(acl);
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        security = bytes;
    }
    public void Refresh(MediaDb db)
    {
        var next = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var timestamp = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        VFileInfo DirectoryInfo(ulong id) => new() { FileAttributes = (uint)FileAttributes.Directory, IndexNumber = id, CreationTime = timestamp, LastAccessTime = timestamp, LastWriteTime = timestamp, ChangeTime = timestamp };
        next["\\"] = new("\\", null, DirectoryInfo(1));
        next["\\mediahub"] = new("\\mediahub", null, DirectoryInfo(2));
        for (var i = 0; i < Categories.Length; i++) next["\\mediahub\\" + Categories[i]] = new("\\mediahub\\" + Categories[i], null, DirectoryInfo((ulong)(i + 3)));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(db.SearchItems("", "", true)));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("isDirectory").GetBoolean()) continue;
            try
            {
                var source = PathGuard.EnsureAllowed(item.GetProperty("path").GetString()!);
                if (source.StartsWith(MountPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new System.IO.FileInfo(source); if (!info.Exists) continue;
                var category = item.GetProperty("category").GetString()!; if (!Categories.Contains(category)) category = "其他";
                var name = item.GetProperty("name").GetString()!;
                name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd(' ', '.');
                if (string.IsNullOrEmpty(name)) name = info.Name;
                if (string.IsNullOrEmpty(Path.GetExtension(name))) name += info.Extension;
                var id = item.GetProperty("id").GetInt64();
                var path = "\\mediahub\\" + category + "\\" + name;
                if (next.ContainsKey(path)) path = "\\mediahub\\" + category + "\\" + Path.GetFileNameWithoutExtension(name) + "_" + id + Path.GetExtension(name);
                var size = (ulong)info.Length;
                next[path] = new(path, source, new VFileInfo { FileAttributes = (uint)FileAttributes.ReadOnly, FileSize = size, AllocationSize = (size + 4095) / 4096 * 4096, IndexNumber = (ulong)id + 100, CreationTime = (ulong)info.CreationTimeUtc.ToFileTimeUtc(), LastWriteTime = (ulong)info.LastWriteTimeUtc.ToFileTimeUtc(), LastAccessTime = (ulong)info.LastAccessTimeUtc.ToFileTimeUtc(), ChangeTime = (ulong)info.LastWriteTimeUtc.ToFileTimeUtc() });
            }
            catch { /* unavailable originals are omitted */ }
        }
        Volatile.Write(ref nodes, next);
    }
    public void Start(MediaDb db)
    {
        if (IsMounted) return;
        Refresh(db);
        Directory.CreateDirectory(Path.GetDirectoryName(MountPath)!);
        if (Directory.Exists(MountPath))
        {
            if (Directory.EnumerateFileSystemEntries(MountPath).Any()) throw new IOException("WinFsp 挂载目录必须为空。" );
            Directory.Delete(MountPath, false);
        }
        var candidate = new FileSystemHost(this);
        var status = candidate.Mount(MountPath, security);
        if (status < 0) { candidate.Dispose(); Error = $"WinFsp mount: 0x{status:X8}"; throw new IOException(Error); }
        host = candidate; Error = null;
    }
    public override int Init(object hostObject)
    {
        var h = (FileSystemHost)hostObject;
        h.SectorSize = 512; h.SectorsPerAllocationUnit = 8; h.FileInfoTimeout = 0;
        h.CaseSensitiveSearch = false; h.CasePreservedNames = true; h.UnicodeOnDisk = true; h.PersistentAcls = true;
        h.FileSystemName = "MediaHub";
        return STATUS_SUCCESS;
    }
    public override int GetVolumeInfo(out VolumeInfo info) { info = new() { TotalSize = 1UL << 40, FreeSize = 0 }; info.SetVolumeLabel("MediaHub"); return STATUS_SUCCESS; }
    public override int GetSecurityByName(string path, out uint attributes, ref byte[] descriptor)
    {
        attributes = 0; if (!nodes.TryGetValue(path, out var node)) return STATUS_OBJECT_NAME_NOT_FOUND;
        attributes = node.Info.FileAttributes; if (descriptor is not null) descriptor = security; return STATUS_SUCCESS;
    }
    public override int Open(string path, uint options, uint access, out object node, out object desc, out VFileInfo info, out string normalized)
    {
        node = null!; desc = null!; info = default; normalized = null!;
        if ((access & 0x00010116u) != 0 || (options & FILE_DELETE_ON_CLOSE) != 0) return STATUS_ACCESS_DENIED;
        if (!nodes.TryGetValue(path, out var found)) return STATUS_OBJECT_NAME_NOT_FOUND;
        try
        {
            if ((options & FILE_DIRECTORY_FILE) != 0 && found.Source is not null) return STATUS_NOT_A_DIRECTORY;
            if ((options & FILE_NON_DIRECTORY_FILE) != 0 && found.Source is null) return STATUS_FILE_IS_A_DIRECTORY;
            if (found.Source is not null) desc = new FileStream(PathGuard.EnsureAllowed(found.Source), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            node = found; info = found.Info; normalized = found.VirtualPath; return STATUS_SUCCESS;
        }
        catch { return STATUS_ACCESS_DENIED; }
    }
    public override void Close(object node, object desc) => (desc as IDisposable)?.Dispose();
    public override int GetFileInfo(object node, object desc, out VFileInfo info) { info = ((Node)node).Info; return STATUS_SUCCESS; }
    public override int GetSecurity(object node, object desc, ref byte[] descriptor) { descriptor = security; return STATUS_SUCCESS; }
    public override int Read(object node, object desc, IntPtr buffer, ulong offset, uint length, out uint transferred)
    {
        transferred = 0; if (desc is not FileStream stream) return STATUS_FILE_IS_A_DIRECTORY;
        if (offset > long.MaxValue || length > 16 * 1024 * 1024) return STATUS_INVALID_PARAMETER;
        try { var data = new byte[length]; var count = RandomAccess.Read(stream.SafeFileHandle, data, (long)offset); Marshal.Copy(data, 0, buffer, count); transferred = (uint)count; return count == 0 ? STATUS_END_OF_FILE : STATUS_SUCCESS; }
        catch { return STATUS_IO_DEVICE_ERROR; }
    }
    public override bool ReadDirectoryEntry(object node, object desc, string pattern, string marker, ref object context, out string name, out VFileInfo info)
    {
        name = null!; info = default;
        if (context is not IEnumerator<Node> iterator)
        {
            var parent = ((Node)node).VirtualPath;
            iterator = nodes.Values.Where(n => n.VirtualPath != "\\" && Path.GetDirectoryName(n.VirtualPath) == parent).OrderBy(n => Path.GetFileName(n.VirtualPath), StringComparer.OrdinalIgnoreCase).Where(n => marker is null || StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(n.VirtualPath), marker) > 0).GetEnumerator();
            context = iterator;
        }
        if (!iterator.MoveNext()) return false;
        name = Path.GetFileName(iterator.Current.VirtualPath); info = iterator.Current.Info; return true;
    }
    public void Dispose() { host?.Dispose(); host = null; }
}

sealed class VirtualFileSystemService(VirtualFileSystem fileSystem, MediaDb db, ILogger<VirtualFileSystemService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken token) { try { fileSystem.Start(db); } catch (Exception ex) { logger.LogError(ex, "WinFsp mount failed"); } return Task.CompletedTask; }
    public Task StopAsync(CancellationToken token) { fileSystem.Dispose(); return Task.CompletedTask; }
}
