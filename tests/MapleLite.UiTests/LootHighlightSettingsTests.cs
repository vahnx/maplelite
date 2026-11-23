using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.UiTests;

public class LootHighlightSettingsTests
{
    [AvaloniaFact]
    public void Search_add_color_save_and_remove_an_item()
    {
        List<LootItemHighlight> saved = [];
        var panel = Assert.IsType<Expander>(LootHighlightSettings.Create(new(), ["Red Potion", "Blue Potion", "Scroll"],
            (_, items, _) => saved = items, new TextBlock()));
        panel.IsExpanded = true;
        var window = new Window { Width = 820, Height = 900, Content = panel };
        window.Styles.Add(new StyleInclude(new Uri("avares://MapleLite.UiTests/"))
        {
            Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml"),
        });
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var search = panel.GetVisualDescendants().OfType<AutoCompleteBox>().Single();
            var suggestions = Array.Empty<string>();
            search.Populated += (_, args) => suggestions = args.Data.Cast<string>().ToArray();
            search.GetVisualDescendants().OfType<TextBox>().Single().Focus();
            window.KeyTextInput("potion");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Blue Potion", "Red Potion"], suggestions);

            search.SelectedItem = "Red Potion";
            Click("Add item");
            var picker = panel.GetVisualDescendants().OfType<ColorPicker>()
                .Single(control => AutomationProperties.GetName(control) == "Highlight color for Red Potion");
            Assert.NotNull(picker.Template);
            picker.Color = Color.Parse("#FF1122");
            Click("Save drop highlights");
            var item = Assert.Single(saved);
            Assert.Equal("Red Potion", item.Name);
            Assert.Equal(picker.Color, Color.Parse(item.Color));

            Click("Remove");
            Click("Save drop highlights");
            Assert.Empty(saved);
        }
        finally { window.Close(); }

        void Click(string label)
        {
            var button = panel.GetVisualDescendants().OfType<Button>().Single(control => Equals(control.Content, label));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }
}
