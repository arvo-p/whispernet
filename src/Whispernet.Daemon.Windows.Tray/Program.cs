using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Whispernet.Daemon.Client;
using Whispernet.Daemon.Contracts;

namespace Whispernet.Daemon.Windows.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var context = new WhispernetTrayApplicationContext();
        Application.Run(context);
    }
}

internal sealed class WhispernetTrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private DaemonMeshTransport? _client;
    private DaemonStatusDto? _lastStatus;

    public WhispernetTrayApplicationContext()
    {
        _statusItem = new ToolStripMenuItem("Whispernet Service: Connecting...") { Enabled = false };
        var openUiItem = new ToolStripMenuItem("Open Whispernet", null, (_, _) => LaunchWpfClient());
        var restartItem = new ToolStripMenuItem("Start / Restart Daemon", null, (_, _) => _ = RestartDaemonAsync());
        var copyNodeIdItem = new ToolStripMenuItem("Copy NodeId", null, (_, _) => CopyText(_lastStatus?.SelfNodeId));
        var copyTicketItem = new ToolStripMenuItem("Copy Iroh Ticket", null, (_, _) => CopyText(_lastStatus?.SelfTicket));
        var exitItem = new ToolStripMenuItem("Exit Whispernet (Stop Daemon)", null, (_, _) => _ = ShutdownDaemonAndExitAsync());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            openUiItem,
            restartItem,
            copyNodeIdItem,
            copyTicketItem,
            new ToolStripSeparator(),
            exitItem
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Whispernet Daemon (Connecting...)",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => LaunchWpfClient();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _pollTimer.Tick += async (_, _) => await RefreshDaemonStatusAsync().ConfigureAwait(true);
        _pollTimer.Start();
        _ = RefreshDaemonStatusAsync();
    }

    private async Task RefreshDaemonStatusAsync()
    {
        try
        {
            if (_client is null)
            {
                _client = new DaemonMeshTransport();
                await _client.ConnectAsync().ConfigureAwait(true);
            }

            DaemonStatusDto status = await _client.GetStatusAsync().ConfigureAwait(true);
            _lastStatus = status;
            string shortId = status.SelfNodeId.Length > 8 ? status.SelfNodeId[..8] : status.SelfNodeId;
            _statusItem.Text = $"Online ({shortId}..) · {status.OnlinePeerCount} peers · {status.HeldDtnBundleCount} DTN bundles";
            _notifyIcon.Text = $"Whispernet: Online ({status.OnlinePeerCount} peers)";
        }
        catch
        {
            _client?.Dispose();
            _client = null;
            _statusItem.Text = "Whispernet Service: Offline (Start WhispernetDaemon service)";
            _notifyIcon.Text = "Whispernet Service: Offline";
        }
    }

    private static void LaunchWpfClient()
    {
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string wpfExe = Path.Combine(exeDir, "Whispernet.Client.Wpf.exe");
            if (File.Exists(wpfExe))
            {
                Process.Start(new ProcessStartInfo(wpfExe) { UseShellExecute = true });
            }
        }
        catch
        {
        }
    }

    private static void CopyText(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Clipboard.SetText(value);
        }
    }

    private async Task RestartDaemonAsync()
    {
        try
        {
            _statusItem.Text = "Whispernet Service: Restarting...";
            if (_client is not null)
            {
                try { await _client.RequestShutdownAsync().ConfigureAwait(true); } catch { }
                _client.Dispose();
                _client = null;
                await Task.Delay(800).ConfigureAwait(true);
            }

            // Start the Windows Service (requests UAC elevation if current user lacks SERVICE_START)
            using Process? p = Process.Start(new ProcessStartInfo("sc.exe", "start WhispernetDaemon")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (p is not null)
            {
                await p.WaitForExitAsync().ConfigureAwait(true);
            }

            await RefreshDaemonStatusAsync().ConfigureAwait(true);
        }
        catch
        {
            _statusItem.Text = "Whispernet Service: Restart canceled or failed";
        }
    }

    private async Task ShutdownDaemonAndExitAsync()
    {
        try
        {
            if (_client is not null)
            {
                await _client.RequestShutdownAsync().ConfigureAwait(true);
            }
        }
        catch
        {
        }
        finally
        {
            ExitThread();
        }
    }

    protected override void ExitThreadCore()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _client?.Dispose();
        base.ExitThreadCore();
    }
}
