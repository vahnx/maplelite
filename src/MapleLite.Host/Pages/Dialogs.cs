using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Pages;

internal static class Dialogs
{
    /// <summary>A small themed yes/no dialog. True when the user picks <paramref name="ok"/>.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string ok, string cancel = "Cancel")
    {
        var dialog = new Window
        {
            Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        }.Res(Window.BackgroundProperty, ThemeTokens.Surface);
        var yes = new Button { Content = ok, IsDefault = true };
        yes.Classes.Add("accent");
        var no = new Button { Content = cancel, IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        var buttons = Ui.Row(8, yes, no);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Stack(16, Ui.Subheading(title), Ui.Text(message), buttons),
        };
        return await dialog.ShowDialog<bool>(owner);
    }
}
