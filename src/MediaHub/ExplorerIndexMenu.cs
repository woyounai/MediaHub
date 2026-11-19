using Microsoft.Win32;

static class ExplorerIndexMenu
{
    public static void Register()
    {
        var executable = Environment.ProcessPath!;
        foreach (var kind in new[] { "*", "Directory" })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{kind}\shell\MediaHubIndex");
            key.SetValue("", "快速加入 MediaHub 索引");
            key.SetValue("Icon", executable);
            using var command = key.CreateSubKey("command");
            command.SetValue("", $"\"{executable}\" --index \"%1\"");
        }
    }
}
