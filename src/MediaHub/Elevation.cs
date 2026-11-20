using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;

// Each privileged operation uses a temporary process; the web server keeps its normal token.
static class Elevation
{
    public static async Task<string> RunAsync(string script)
    {
        if (AdminCheck.IsAdministrator()) return await PowerShell.RunAsync(script);
        var name = "MediaHub-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var wrapper = $$"""
            $ErrorActionPreference='Stop'
            $pipe=New-Object System.IO.Pipes.NamedPipeClientStream('.', '{{name}}', [System.IO.Pipes.PipeDirection]::InOut)
            try {
                $pipe.Connect(30000)
                $reader=New-Object System.IO.StreamReader($pipe)
                $writer=New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
                $writer.AutoFlush=$true
                $code=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($reader.ReadLine()))
                try { $output=(& ([ScriptBlock]::Create($code)) | Out-String); $reply=@{ok=$true; output=$output} }
                catch { $reply=@{ok=$false; error=$_.Exception.Message} }
                $writer.WriteLine(($reply | ConvertTo-Json -Compress))
            } finally { $pipe.Dispose() }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(executable, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })
                ?? throw new IOException("无法启动管理员操作。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("已取消管理员授权，操作未执行。", ex); }
        using (process)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        {
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited) != connected)
                throw new IOException("管理员操作未能建立连接，请使用当前 Windows 账户授权。");
            await connected;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != process.Id)
                throw new IOException("管理员操作连接身份验证失败。");
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(script)));
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("管理员操作未返回结果。");
            using var reply = JsonDocument.Parse(line);
            if (!reply.RootElement.GetProperty("ok").GetBoolean())
                throw new IOException(reply.RootElement.GetProperty("error").GetString());
            return reply.RootElement.GetProperty("output").GetString()?.Trim() ?? "";
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
