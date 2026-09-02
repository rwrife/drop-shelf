using Avalonia;

namespace DropShelf.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        args.Length == 1 && string.Equals(args[0], "--package-smoke-test", StringComparison.Ordinal)
            ? PackagedSmokeTest.RunAsync().GetAwaiter().GetResult()
            : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
