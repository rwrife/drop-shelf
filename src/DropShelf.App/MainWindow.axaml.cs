using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DropShelf.App;

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
