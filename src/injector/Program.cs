namespace GameSpeed;

internal static class Program
{
    /// <summary>Arguments that belong to the overlay's own command line.</summary>
    private static readonly string[] OverlayArgs = { "--replay", "--shot", "--rating" };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => OverlayArgs.Contains(a, StringComparer.OrdinalIgnoreCase)))
            return BgOverlay.Cli.Run(args);
        if (args.Length > 0) return Cli.Run(args);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
