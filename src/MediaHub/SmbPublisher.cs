// Compatibility facade: index changes now refresh the WinFsp namespace.
// No symbolic links, hard links, or copies are created.
static class SmbPublisher
{
    static readonly SemaphoreSlim Gate = new(1);
    static int pending = 1;
    public static void RequestPublish() => Interlocked.Exchange(ref pending, 1);
    public static bool TakePending() => Interlocked.Exchange(ref pending, 0) != 0;
    public static void Register(string root) { }
    public static Task RemoveLinksAsync(string source) => Task.CompletedTask;
    public static async Task<int> RemoveIndexesAsync(MediaDb db, long[]? ids = null, string? path = null)
    {
        await Gate.WaitAsync();
        try
        {
            var removed = ids is not null ? db.RemoveIndexIds(ids.Distinct()) : db.RemovePathAndChildren(path!);
            VirtualFileSystem.Current?.Refresh(db);
            return removed;
        }
        finally { Gate.Release(); }
    }
    public static async Task PublishAsync(MediaDb db)
    {
        await Gate.WaitAsync();
        try { VirtualFileSystem.Current?.Refresh(db); }
        finally { Gate.Release(); }
    }
}
