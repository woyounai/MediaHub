using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class StartupService
{
    public static async Task SetEnabled(bool enabled)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法获取当前 Windows 用户。");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取应用路径。");
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请直接运行 MediaHub.exe 后设置开机启动。");
        var command = $"\"{exe}\" --background";
        if (enabled && command.Length > 260) throw new InvalidOperationException("应用路径过长，请移到较短的路径后设置启动项。");
        var name = PowerShell.Quote("MediaHub-" + sid);
        var existing = await PowerShell.RunAsync($"$ErrorActionPreference='Stop'; if(Get-ScheduledTask -TaskName {name} -ErrorAction SilentlyContinue){{'EXISTS'}}");
        if (existing == "EXISTS")
            await Elevation.RunAsync($"$ErrorActionPreference='Stop'; Get-ScheduledTask -TaskName {name} | Unregister-ScheduledTask -Confirm:$false");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("MediaHub", command);
        else key.DeleteValue("MediaHub", false);
    }
    public static void HideConsole()
    {
        var window = GetConsoleWindow();
        if (window != IntPtr.Zero) ShowWindow(window, 0);
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
}
