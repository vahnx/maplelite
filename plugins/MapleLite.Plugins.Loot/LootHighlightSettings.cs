using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MapleLite.Core.Market;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.Loot;

internal static class LootHighlightSettings
{
    public static Control Create(LootSettings settings, IEnumerable<string> itemNames,
        Action<List<LootPriceRange>, List<LootItemHighlight>, bool> save, TextBlock priceStatus)
    {
        const string defaultItemColor = "#FFB54D";
        var knownNames = itemNames.Concat(settings.ItemHighlights.Select(item => item.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = new AutoCompleteBox
        {
            Watermark = "Search items…",
            MinWidth = 250,
            MaxDropDownHeight = 240,
            FilterMode = AutoCompleteFilterMode.Contains,
            ItemsSource = knownNames.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        AutomationProperties.SetName(search, "Search items to highlight");
        var itemRows = new StackPanel { Spacing = 4 };
        var itemColors = new Dictionary<string, ColorPicker>(StringComparer.OrdinalIgnoreCase);
        var itemFeedback = Muted("");

        void AddItem(string name, string color)
        {
            if (itemColors.ContainsKey(name)) return;
            var picker = CreatePicker(color, defaultItemColor);
            AutomationProperties.SetName(picker, $"Highlight color for {name}");
            itemColors.Add(name, picker);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var label = Text(name);
            label.VerticalAlignment = VerticalAlignment.Center;
            var remove = Button("Remove", () =>
            {
                itemColors.Remove(name);
                itemRows.Children.Remove(row);
                itemFeedback.Text = "";
            });
            AutomationProperties.SetName(remove, $"Remove highlight for {name}");
            row.Children.Add(label);
            row.Children.Add(picker);
            row.Children.Add(remove);
            itemRows.Children.Add(row);
        }

        foreach (var item in settings.ItemHighlights)
            if (!string.IsNullOrWhiteSpace(item.Name)) AddItem(item.Name, item.Color);

        var addItem = Button("Add item", () =>
        {
            if (!knownNames.TryGetValue(search.Text?.Trim() ?? "", out var name))
            {
                itemFeedback.Text = "Select an item from the search suggestions.";
                return;
            }
            if (itemColors.ContainsKey(name))
            {
                itemFeedback.Text = "That item already has a highlight.";
                return;
            }
            AddItem(name, defaultItemColor);
            search.SelectedItem = null;
            search.Text = "";
            itemFeedback.Text = "";
        });

        var defaults = LootPriceRange.Defaults();
        var rows = new List<(TextBox Min, TextBox Max, ColorPicker Color)>();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };

        void Add(int row, int column, Control control)
        {
            control.Margin = new Thickness(0, 2, 8, 2);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Add(0, 0, Muted("Min price"));
        Add(0, 1, Muted("Max price"));
        Add(0, 2, Muted("Color"));
        for (int i = 0; i < defaults.Count; i++)
        {
            var range = i < settings.PriceRanges.Count ? settings.PriceRanges[i] : defaults[i];
            var min = new TextBox { Text = range.Min?.ToString(CultureInfo.InvariantCulture), Watermark = "Any", MinWidth = 100 };
            var max = new TextBox { Text = range.Max?.ToString(CultureInfo.InvariantCulture), Watermark = "Any", MinWidth = 100 };
            var color = CreatePicker(range.Color, defaults[i].Color);
            AutomationProperties.SetName(min, $"Range {i + 1} minimum price");
            AutomationProperties.SetName(max, $"Range {i + 1} maximum price");
            AutomationProperties.SetName(color, $"Range {i + 1} color");
            rows.Add((min, max, color));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Add(i + 1, 0, min);
            Add(i + 1, 1, max);
            Add(i + 1, 2, color);
        }

        var overlay = new CheckBox { Content = "Show drop alerts over the game", IsChecked = settings.ShowDropOverlay };
        ToolTip.SetTip(overlay, "Requires the game overlay to be enabled in MapleLite Settings.");
        var feedback = Muted("");
        void Save()
        {
            var ranges = new List<LootPriceRange>();
            foreach (var (min, max, color) in rows)
            {
                long? low = MesoAmount.Parse(min.Text ?? ""), high = MesoAmount.Parse(max.Text ?? "");
                if ((!string.IsNullOrWhiteSpace(min.Text) && low is null) || (!string.IsNullOrWhiteSpace(max.Text) && high is null))
                {
                    feedback.Text = $"Range {ranges.Count + 1} needs a valid meso amount.";
                    return;
                }
                if (low > high)
                {
                    feedback.Text = $"Range {ranges.Count + 1} has a minimum higher than its maximum.";
                    return;
                }
                ranges.Add(new LootPriceRange(low, high, color.Color.ToString()));
            }

            try
            {
                var items = itemColors.Select(item => new LootItemHighlight(item.Key, item.Value.Color.ToString())).ToList();
                save(ranges, items, overlay.IsChecked == true);
                feedback.Text = "Drop highlights saved.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                feedback.Text = "Couldn't save drop highlights. " + e.Message;
            }
        }

        return new Expander
        {
            Header = "Drop highlights",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Stack(8,
                Title("Specific items"),
                Muted("Item colors take priority over price ranges."),
                Wrap(search, addItem), itemFeedback, itemRows,
                Title("Price ranges"),
                Muted("Ranges use the archived price per item (e.g. 500k or 2m). Both bounds are inclusive. The first matching row wins."),
                Muted("Leave one bound blank for an open-ended range, or both blank to disable a row."),
                grid, overlay, Wrap(Button("Save drop highlights", Save), feedback), priceStatus),
        };
    }

    static ColorPicker CreatePicker(string color, string fallback) => new()
    {
        Color = Color.TryParse(color, out var selected) ? selected : Color.Parse(fallback),
        IsAlphaEnabled = false,
    };
}
