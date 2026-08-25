using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(DropShelf.App.Tests.TestAppBuilder))]

namespace DropShelf.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        Program.BuildAvaloniaApp()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
