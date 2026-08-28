using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DropShelf.Core;
using DropShelf.Infrastructure;

namespace DropShelf.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string databasePath = Path.Combine(localData, "DropShelf", "shelf.db");
            ISettingsStore settingsStore = new SqliteShelfStore(databasePath);
            MainWindow? window = null;
            window = new MainWindow(AppSettings.Default, retryShelfLoad: async () =>
            {
                AppSettings recovered = await LoadStartupSettingsForHostAsync(settingsStore);
                window!.ApplySettings(recovered);
            });
            desktop.MainWindow = window;
            _ = LoadAndApplyStartupSettingsForHostAsync(window, settingsStore);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static Task<AppSettings> LoadStartupSettingsForHostAsync(
        ISettingsStore settingsStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        return settingsStore.LoadSettingsAsync(cancellationToken);
    }

    public static async Task LoadAndApplyStartupSettingsForHostAsync(
        MainWindow window, ISettingsStore settingsStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ShowShelfState(ShelfUiState.Loading);
        try
        {
            AppSettings settings = await LoadStartupSettingsForHostAsync(settingsStore, cancellationToken);
            window.ApplySettings(settings);
            window.ShowShelfState(ShelfUiState.Ready);
        }
        catch
        {
            window.ShowShelfState(ShelfUiState.RecoverableError);
        }
    }
}
