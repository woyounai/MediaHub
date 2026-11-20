using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

static class StartupService
{
    public static async Task SetEnabled(bool enabled)
    {
        if (!AdminCheck.IsAdministrator()) throw new InvalidOperationException("设置开机启动需要管理员权限，请以管理员身份运行 MediaHub。");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法获取当前 Windows 用户。");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取应用路径。");
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请直接运行 MediaHub.exe 后设置开机启动。");
        await PowerShell.RunAsync(BuildScript(enabled, exe, sid));
        // Keep the legacy entry until Task Scheduler accepts the configuration.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("MediaHub", false);
    }

    internal static string BuildScript(bool enabled, string exe, string sid)
    {
        var name = PowerShell.Quote("MediaHub-" + sid);
        if (!enabled) return $"$ErrorActionPreference='Stop'; $task=Get-ScheduledTask -TaskName {name} -ErrorAction SilentlyContinue; if ($task) {{ $task | Unregister-ScheduledTask -Confirm:$false }}";
        var launch = $"$ErrorActionPreference='Stop'; Start-Process -FilePath {PowerShell.Quote(exe)} -WorkingDirectory {PowerShell.Quote(Path.GetDirectoryName(exe)!)} -ArgumentList '--background' -WindowStyle Hidden";
        var arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(launch));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        return $$"""
            $ErrorActionPreference='Stop'
            $action=New-ScheduledTaskAction -Execute {{PowerShell.Quote(powershell)}} -Argument {{PowerShell.Quote(arguments)}} -WorkingDirectory {{PowerShell.Quote(Path.GetDirectoryName(exe)!)}}
            $trigger=New-ScheduledTaskTrigger -AtLogOn -User {{PowerShell.Quote(sid)}}
            $principal=New-ScheduledTaskPrincipal -UserId {{PowerShell.Quote(sid)}} -LogonType Interactive -RunLevel Highest
            $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
            Register-ScheduledTask -TaskName {{name}} -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
            $task=Get-ScheduledTask -TaskName {{name}}
            if ($task.State -eq 'Disabled' -or $task.Principal.RunLevel -ne 'Highest') { throw '开机启动任务验证失败。' }
            """;
    }
}

