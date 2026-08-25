using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace DropShelf.App.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void EmptyShelfWindowExposesClearInitialState()
    {
        MainWindow window = new();
        TextBlock? emptyState = window.FindControl<TextBlock>("EmptyShelfMessage");

        window.Show();

        Assert.NotNull(emptyState);
        Assert.True(window.IsVisible);
        Assert.Equal("Drop Shelf", window.Title);
        Assert.Equal("Drop files, text, or URLs here", emptyState.Text);

        window.Close();
    }
}
