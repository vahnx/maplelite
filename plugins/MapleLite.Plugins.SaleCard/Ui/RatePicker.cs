using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// The going rate under an item's name, as buttons: one per source ("meso.watch 5,999", "FM 6k") and, when there is
/// more than one, "Average". Clicking one puts that figure in the item's price box. Follows the row's DataContext,
/// so it works in the recycled Selling list as well as on the Buying rows.
/// </summary>
internal sealed class RatePicker : WrapPanel
{
    SaleItem? _item;

    public RatePicker()
    {
        Orientation = Orientation.Horizontal;
        DataContextChanged += (_, _) => Follow(DataContext as SaleItem);
        DetachedFromVisualTree += (_, _) => Follow(null);
        AttachedToVisualTree += (_, _) => Follow(DataContext as SaleItem);
    }

    void Follow(SaleItem? item)
    {
        if (_item != null) _item.PropertyChanged -= Changed;
        _item = item;
        if (_item != null) _item.PropertyChanged += Changed;
        Rebuild();
    }

    void Changed(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SaleItem.Rate) or nameof(SaleItem.Custom)) Rebuild();
    }

    void Rebuild()
    {
        Children.Clear();
        var it = _item;
        if (it == null || it.RateParts.Count == 0) { IsVisible = false; return; }
        IsVisible = true;
        ToolTip.SetTip(this, it.Rate);
        var choices = it.RateParts.Select(p => (label: p.Label == "FM" ? "FM ask" : p.Label, price: p.Price)).ToList();
        if (choices.Count > 1 && it.RateValue is long avg) choices.Add(("Average", avg));
        long? current = string.IsNullOrWhiteSpace(it.Custom) ? null : Mesos.Parse(it.Custom);
        foreach (var (label, price) in choices)
        {
            var text = Mesos.Format(price);                 // short, as the card prints it ("6k")
            var exact = price.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);   // what goes in the box ("5,999")
            var b = new Button
            {
                Content = $"{label} {text}", FontSize = 11, Padding = new Thickness(6, 1), MinHeight = 0,
                Margin = new Thickness(0, 2, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            b.Themed(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, current == price ? ThemeTokens.Text : ThemeTokens.Accent);
            if (current == price) b.FontWeight = FontWeight.SemiBold;
            ToolTip.SetTip(b, current == price ? $"Your price is {exact} ({label})" : $"Put {exact} ({label}) in the price box");
            b.Click += (_, _) => { if (_item != null) _item.Custom = exact; };
            Children.Add(b);
        }
    }
}
