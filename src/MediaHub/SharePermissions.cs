using System.Security.Principal;
using System.Text.Json;

static class SharePermissions
{
    public record Entry(string Account, string Sid, string Access);
    public record Update(Entry[] Users);
    static readonly SemaphoreSlim Gate = new(1);
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaHub", "share-permissions.json");
    public static Entry[] Load()
    {
        if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(FilePath)) ?? [];
        var json = PowerShell.RunAsync("$ProgressPreference='SilentlyContinue'; @(Get-SmbShare | Where-Object {$_.Description -eq 'MediaHub managed share' -or $_.Name -eq 'mediahub'} | Get-SmbShareAccess | Where-Object {$_.AccessControlType -eq 'Allow'} | Select-Object -ExpandProperty AccountName -Unique) | ConvertTo-Json -Compress").GetAwaiter().GetResult();
        var accounts = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
        if (accounts.Length == 0) accounts = [WindowsIdentity.GetCurrent().Name];
        var users = accounts.Select(account => Resolve(account, "Read")).ToArray();
        Save(users); return users;
    }
    static Entry Resolve(string account, string access)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Any(char.IsControl) || access is not ("Read" or "None")) throw new ArgumentException("请选择有效账户及读取/禁止访问权限。");
        var sid = (SecurityIdentifier)new NTAccount(account.Trim()).Translate(typeof(SecurityIdentifier));
        var canonical = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
        return new(canonical, sid.Value, access);
    }
    static void Save(Entry[] users)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(users));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/smb/permissions", () => Results.Ok(new { users = Load(), mode = "read-only" }));
        app.MapGet("/api/smb/users", async () =>
        {
            try
            {
                var json = await PowerShell.RunAsync("$ProgressPreference='SilentlyContinue'; @(Get-LocalUser | Where-Object Enabled | ForEach-Object { $env:COMPUTERNAME+'\\'+$_.Name }) | ConvertTo-Json -Compress");
                return Results.Ok(new { accounts = JsonSerializer.Deserialize<string[]>(json) ?? [] });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/smb/permissions", async (Update request, VirtualFileSystem fs, MediaDb db) =>
        {
            if (!AdminCheck.IsAdministrator()) return Results.Conflict(new { error = "修改共享权限需要管理员权限。" });
            if (request.Users is null || request.Users.Length > 100) return Results.BadRequest(new { error = "最多配置 100 个账户。" });
            await Gate.WaitAsync();
            var previous = Load();
            try
            {
                var users = request.Users.Select(u => Resolve(u.Account, u.Access)).ToArray();
                if (users.Select(u => u.Sid).Distinct().Count() != users.Length) throw new ArgumentException("账户重复。" );
                Save(users);
                await ApplySmb(users);
                fs.Dispose(); fs.ReloadPermissions(); fs.Start(db);
                return Results.Ok(new { ok = true, users, note = "两层权限已应用，请客户端重新连接。" });
            }
            catch (Exception ex)
            {
                Save(previous);
                try { await ApplySmb(previous); fs.Dispose(); fs.ReloadPermissions(); fs.Start(db); } catch { }
                return Results.BadRequest(new { error = ex.Message });
            }
            finally { Gate.Release(); }
        });
    }
    public static async Task ApplySmb(Entry[] users)
    {
        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; $shares=@(Get-SmbShare | Where-Object {$_.Description -eq 'MediaHub managed share' -or $_.Name -eq 'mediahub'}); foreach($share in $shares){" +
            "Get-SmbShareAccess -Name $share.Name | ForEach-Object { if($_.AccessControlType -eq 'Deny'){Unblock-SmbShareAccess -Name $share.Name -AccountName $_.AccountName -Force -Confirm:$false | Out-Null}else{Revoke-SmbShareAccess -Name $share.Name -AccountName $_.AccountName -Force -Confirm:$false | Out-Null} };";
        foreach (var user in users.Where(u => u.Access == "Read")) script += $"Grant-SmbShareAccess -Name $share.Name -AccountName {PowerShell.Quote(user.Account)} -AccessRight Read -Force | Out-Null;";
        foreach (var user in users.Where(u => u.Access == "None")) script += $"Block-SmbShareAccess -Name $share.Name -AccountName {PowerShell.Quote(user.Account)} -Force | Out-Null;";
        await PowerShell.RunAsync(script + "}");
    }
}
