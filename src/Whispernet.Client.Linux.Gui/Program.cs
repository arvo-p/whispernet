using Aprillz.MewUI;
using Whispernet.App.Messaging;

namespace Whispernet.Client.Linux.Gui;

internal static class Program
{
    public static int Main(string[] args)
    {
        string? rootDir = ParseRootOption(args);
        string effectiveRoot = string.IsNullOrWhiteSpace(rootDir)
            ? WhispernetPaths.GetDataDirectory()
            : Path.GetFullPath(rootDir);

        var accountService = new AccountService(effectiveRoot);
        var gui = new WhispernetGuiWindow(effectiveRoot, accountService);

        if (args.Contains("--headless-check", StringComparer.OrdinalIgnoreCase))
        {
            X11Platform.Register();
            MewVGX11Backend.Register();
            gui.InitializeAsync().GetAwaiter().GetResult();
            Console.WriteLine($"[GUI-CHECK] Whispernet.Client.Linux.Gui initialized successfully (root='{effectiveRoot}').");
            return 0;
        }

        X11Platform.Register();
        MewVGX11Backend.Register();

        Window window = gui.BuildWindow();
        Application.Run(window);
        gui.Shutdown();
        return 0;
    }

    private static string? ParseRootOption(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--root", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
