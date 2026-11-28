using Whispernet.App.Messaging;
using Whispernet.Daemon.Core;

namespace Whispernet.Daemon.Linux;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? rootDir = ParseOption(args, "--root", "--data-dir");
        string dataDir = string.IsNullOrWhiteSpace(rootDir)
            ? Path.Combine(WhispernetPaths.GetDataDirectory(), "daemon")
            : Path.GetFullPath(rootDir);

        string alpn = ParseOption(args, "--alpn") ?? "/whispernet/direct/1";
        string? busAddress = ParseOption(args, "--dbus");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        await using var host = new WhispernetDaemonHost(dataDir, alpn);
        host.LogEmitted += msg => Console.WriteLine(msg);

        await host.StartAsync(cts.Token).ConfigureAwait(false);
        await using var dbus = new LinuxDBusServer(host, busAddress);
        await dbus.StartAsync(cts.Token).ConfigureAwait(false);

        Console.WriteLine($"[whispernetd] Listening on D-Bus (org.whispernet.Daemon /org/whispernet/Daemon), dataDir='{dataDir}'.");
        Console.WriteLine($"[whispernetd] SelfId={host.SelfId.Value}");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("[whispernetd] Shutting down...");
        return 0;
    }

    private static string? ParseOption(string[] args, params string[] names)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (names.Any(n => args[i].Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
