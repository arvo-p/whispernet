using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whispernet.Daemon.Contracts;
using Whispernet.Daemon.Core;

namespace Whispernet.Daemon.Windows.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        IHost host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "WhispernetDaemon";
            })
            .ConfigureServices((_, services) =>
            {
                services.AddHostedService<WhispernetWindowsBackgroundService>();
            })
            .Build();

        await host.RunAsync().ConfigureAwait(false);
    }
}

internal sealed class WhispernetWindowsBackgroundService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private WhispernetDaemonHost? _daemonHost;
    private WindowsNamedPipeServer? _pipeServer;

    public WhispernetWindowsBackgroundService(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        string dataDir = Path.Combine(programData, "Whispernet", "daemon");
        _daemonHost = new WhispernetDaemonHost(dataDir);
        _daemonHost.ShutdownRequested += () => _lifetime.StopApplication();
        await _daemonHost.StartAsync(stoppingToken).ConfigureAwait(false);

        _pipeServer = new WindowsNamedPipeServer(_daemonHost);
        _pipeServer.Start(DaemonIpcConstants.WindowsPipeName, stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync().ConfigureAwait(false);
        }

        if (_daemonHost is not null)
        {
            await _daemonHost.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
