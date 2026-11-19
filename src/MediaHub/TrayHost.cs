using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

sealed class TrayHost : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationContext? context;
    private Control? dispatcher;

    public TrayHost(IHostApplicationLifetime lifetime, string url)
    {
        thread = new Thread(() =>
        {
            using var control = new Control();
            control.CreateControl();
            dispatcher = control;
            using var menu = new ContextMenuStrip();
            void Open() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            menu.Items.Add("打开 MediaHub", null, (_, _) => Open());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 MediaHub", null, (_, _) => lifetime.StopApplication());
            using var icon = new NotifyIcon
            {
                Text = "MediaHub — 右键退出",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
            };
            icon.DoubleClick += (_, _) => Open();
            using var appContext = new ApplicationContext();
            context = appContext;
            ready.SetResult();
            Application.Run(appContext);
            icon.Visible = false;
        }) { IsBackground = true, Name = "MediaHub tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (dispatcher is { IsDisposed: false } control)
            control.BeginInvoke(() => context?.ExitThread());
        thread.Join(TimeSpan.FromSeconds(5));
    }
}
