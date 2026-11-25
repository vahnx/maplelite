using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MapleLite.Core.Game;
using MapleLite.Host.Themes;
using MapleLite.Plugins.Loot;
using MapleLite.Sdk.Theming;
using Xunit;

namespace MapleLite.UiTests;

public class LootViewTests
{
    [AvaloniaFact]
    public void MissingItemIconsFollowThemeChangesInTotalsAndRecentPickups()
    {
        var folder = Directory.CreateTempSubdirectory("maplelite-loot-view-");
        var log = new LootLog(folder.FullName);
        var now = DateTimeOffset.Now;
        log.Add(new LootRecord(now, "Item", "Unknown drop", 1, "", "test"));
        log.Add(new LootRecord(now, "Item", "Blue Potion", 1, "", "test"));
        var catalog = ItemCatalog.LoadShipped();
        var view = new LootView(log, new ItemNames(catalog), now.AddMinutes(-1), new LootHighlights(catalog));
        var window = new Window { Width = 900, Height = 640, Content = view };
        var themes = new ThemeService();
        try
        {
            themes.Apply(BuiltInThemes.Midnight);
            window.Show();
            view.Refresh();
            Dispatcher.UIThread.RunJobs();

            var marks = view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Text == "?").ToArray();
            var placeholders = marks.Select(mark => Assert.IsType<Grid>(mark.Parent)).ToArray();
            Assert.Equal(new[] { 24d, 32d }, placeholders.Select(icon => icon.Width).Order());
            var outlines = placeholders.Select(icon => Assert.Single(icon.Children.OfType<Rectangle>())).ToArray();
            Assert.All(outlines, outline => Assert.Equal(new[] { 3d, 2d }, outline.StrokeDashArray));
            Assert.Equal(2, view.GetVisualDescendants().OfType<Image>().Count(image => image.Source != null));
            AssertTheme(BuiltInThemes.Midnight);

            themes.Apply(BuiltInThemes.Daybreak);
            Dispatcher.UIThread.RunJobs();
            AssertTheme(BuiltInThemes.Daybreak);

            void AssertTheme(ThemeDefinition theme)
            {
                Assert.All(outlines, outline => Assert.Equal(Color.Parse(theme.Color(ThemeTokens.TextMuted)), Assert.IsAssignableFrom<ISolidColorBrush>(outline.Stroke).Color));
                Assert.All(marks, mark => Assert.Equal(Color.Parse(theme.Color(ThemeTokens.Text)), Assert.IsAssignableFrom<ISolidColorBrush>(mark.Foreground).Color));
            }
        }
        finally
        {
            window.Close();
            themes.Apply(BuiltInThemes.Midnight);
            folder.Delete(recursive: true);
        }
    }
}
