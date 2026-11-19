using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.SaleCard;

/// <summary>Everything on a card besides the items. Mirrors the Sale Card Generator app's Card Design options.</summary>
public sealed record CardOptions
{
    public string Title { get; init; } = "";
    public string Seller { get; init; } = "";
    public string Server { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Location { get; init; } = "";
    public string Tagline { get; init; } = "";
    public int[]? Avatar { get; init; }          // AvatarReader.W x AvatarReader.H, already cut out
    public (int[] px, int w, int h)? Map { get; init; }
    /// <summary>The map's size on the card: 1 fills its room (two tiles wide), down to 0.4.</summary>
    public double MapScale { get; init; } = 1;
    /// <summary>Circle your yellow dot on the map, with an arrow and your IGN beside it.</summary>
    public bool SellerMarker { get; init; }
    public CardRenderer.MarkerColours MarkerColours { get; init; } = new();
    /// <summary>Where you put the name tag: its centre relative to your dot, in map pixels. Null places it automatically.</summary>
    public double[]? MarkerTag { get; init; }
    /// <summary>Add "ea" after the price of cards that hold more than one.</summary>
    public bool Each { get; init; } = true;
    /// <summary>Pack tiles into the shortest column, each at its own height (no gaps under short tiles, nothing stretched).</summary>
    public bool Pack { get; init; } = true;
    /// <summary>Tag each tile SALE (pink) or BUYING (blue).</summary>
    public bool TradeTags { get; init; } = true;
    /// <summary>Tag each priced item you sell with its discount ("-20%") when <see cref="PriceScale"/> is below 1.</summary>
    public bool DiscountTags { get; init; } = true;
    public CardRenderer.TagColours TagColours { get; init; } = new();
    public bool Prices { get; init; } = true;
    public bool Obo { get; init; }
    public bool HideOffer { get; init; }
    public bool Compact { get; init; }
    public bool Stats { get; init; } = true;
    /// <summary>Every price is printed at this share of the list price (the price slider): 1 unchanged.</summary>
    public double PriceScale { get; init; } = 1;
    public string Total { get; init; } = "";
    public string TotalNote { get; init; } = "";
    /// <summary>Tiles in groups by kind, each under a divider in the scheme's accent colour, labelled with the kind.</summary>
    public bool Groups { get; init; } = true;
    /// <summary>The total split by kind, in card order ("Armor", "200k"); shown above the overall total. Empty: overall only.</summary>
    public IReadOnlyList<(string Kind, string Total)> KindTotals { get; init; } = Array.Empty<(string, string)>();
    public string Credit { get; init; } = "";
    public int Theme { get; init; }
    public bool ShowTime { get; init; } = true;
    public string Zone { get; init; } = "";
    public bool MadeWith { get; init; } = true;
    /// <summary>Tag listings live in your Free Market shop and add a line saying how to buy everything else.</summary>
    public bool ShopMarker { get; init; }
    /// <summary>Discord name for that line; empty says "DM me on Discord".</summary>
    public string Discord { get; init; } = "";
    /// <summary>For tests and saved examples: the time printed in the footer. Null means now.</summary>
    public DateTime? Utc { get; init; }
}

/// <summary>
/// Draws the shareable sale card: a seller card (portrait, IGN, server, tagline, total), the minimap, a tile per item
/// and a footer. Same layout and colour schemes as the Sale Card Generator app, built from Avalonia controls and
/// rendered off-screen. Call on the UI thread.
/// </summary>
public static class CardRenderer
{
    /// <summary>A colour scheme for the card: background, card, icon tile, text, quiet text, prices, lines, quantity badge.</summary>
    public sealed record Theme(string Name, string Bg, string Card, string Tile, string Text, string Muted, string Accent, string Line, string BadgeBg, string BadgeFg);

    public static readonly Theme[] Themes =
    {
        new("Dark",        "#14171F", "#1F2430", "#E1DFD5", "#F2F4F8", "#9AA3B5", "#FFC94D", "#2D3445", "#14171F", "#F2F4F8"),
        new("Maple",       "#FBEFD9", "#FFF9EC", "#F1E0BD", "#4A2E17", "#9A7B58", "#D9741A", "#D9BE8E", "#4A2E17", "#FFFDF7"),
        new("Light",       "#F4F6FA", "#FFFFFF", "#E7EBF2", "#1B2230", "#6B7385", "#2563EB", "#D5DBE6", "#1B2230", "#FFFFFF"),
        new("Midnight",    "#0B1026", "#151C3B", "#DDE3F5", "#EEF1FF", "#8E9AC7", "#7FD4FF", "#27305C", "#0B1026", "#EEF1FF"),
        new("Forest",      "#12201A", "#1B2E25", "#E3E8D8", "#EEF5EC", "#93AD9B", "#9BE36B", "#2A4436", "#12201A", "#EEF5EC"),
        new("Sakura",      "#FFF0F5", "#FFFFFF", "#FBE1EA", "#4A2338", "#A97890", "#E0457B", "#F3C4D5", "#4A2338", "#FFFFFF"),
        new("Henesys sky", "#DDF1FF", "#FFFFFF", "#E6F0D8", "#1F3A52", "#6A8AA3", "#2E9B4F", "#B9DAF0", "#1F3A52", "#FFFFFF"),
    };

    /// <summary>Time zones offered for the second time on the card: label, Windows zone id (.NET maps it on macOS and Linux), label in daylight time.</summary>
    public static readonly (string Label, string Id, string Summer)[] Zones =
    {
        ("EST", "Eastern Standard Time", "EDT"), ("CST", "Central Standard Time", "CDT"), ("MST", "Mountain Standard Time", "MDT"),
        ("PST", "Pacific Standard Time", "PDT"), ("AKST", "Alaskan Standard Time", "AKDT"), ("HST", "Hawaiian Standard Time", "HST"),
        ("BRT", "E. South America Standard Time", "BRT"), ("GMT", "GMT Standard Time", "BST"), ("CET", "W. Europe Standard Time", "CEST"),
        ("EET", "E. Europe Standard Time", "EEST"), ("IST", "India Standard Time", "IST"), ("SGT", "Singapore Standard Time", "SGT"),
        ("JST", "Tokyo Standard Time", "JST"), ("KST", "Korea Standard Time", "KST"), ("AEST", "AUS Eastern Standard Time", "AEDT"),
        ("NZST", "New Zealand Standard Time", "NZDT"),
    };

    static readonly FontFamily Font = new("Segoe UI, Inter, Helvetica Neue, Helvetica, Arial, $Default");

    /// <summary>The short name of where you are for the pill: "FM 7" for a Free Market room, "FM Entrance", else nothing.</summary>
    internal static string Spot(string location)
    {
        var m = Regex.Match(location ?? "", @"Free\s*Market\D{0,3}(\d{1,2})", RegexOptions.IgnoreCase);
        if (m.Success) return "FM " + m.Groups[1].Value;
        return Regex.IsMatch(location ?? "", @"Free\s*Market\s*Entrance", RegexOptions.IgnoreCase) ? "FM Entrance" : "";
    }

    /// <summary>"6:42 PM EST" for the picked zone; the day is added when it differs from the UTC day. Null when no zone is picked.</summary>
    internal static string? LocalTime(DateTime utc, string zone)
    {
        var z = Zones.FirstOrDefault(x => x.Label == zone);
        if (z.Id == null) return null;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(z.Id);
            var t = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            string label = tz.IsDaylightSavingTime(t) ? z.Summer : z.Label;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return (t.Date != utc.Date ? t.ToString("MMM d", inv) + "  " : "") + t.ToString("h:mm tt", inv) + " " + label;
        }
        catch { return null; }
    }

    /// <summary>The Seller location marker's colours: the ring and arrow, the name tag, and the tag's text ("#RRGGBB").</summary>
    public sealed record MarkerColours(string Ring = "#E5332A", string Tag = "#E5332A", string Text = "#FFFFFF");

    /// <summary>The item tags' colours ("#RRGGBB"): SALE, BUYING, IN SHOP and the discount tag.</summary>
    public sealed record TagColours(string Sale = TagColours.SaleDefault, string Buy = TagColours.BuyDefault,
        string Shop = TagColours.ShopDefault, string Discount = TagColours.DiscountDefault)
    {
        public const string SaleDefault = "#FF4FA3", BuyDefault = "#2F7DF6", ShopDefault = "#2E9B4F", DiscountDefault = "#FF8A00";
    }

    /// <summary>The discount tag's text for a price slider at <paramref name="scale"/> ("-20%"), or null when nothing is taken off.</summary>
    public static string? DiscountText(double scale)
    {
        int off = (int)Math.Round((1 - scale) * 100, MidpointRounding.AwayFromZero);
        return off >= 1 ? $"-{off}%" : null;
    }

    static IBrush Brush(string hex, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); } catch (FormatException) { return new SolidColorBrush(Color.Parse(fallback)); }
    }

    /// <summary>
    /// "You are here" on the map: a ring round your dot at (cx, cy), and a name tag with an arrow to it. The tag goes
    /// where <paramref name="tagAt"/> puts its centre (relative to the dot), else on the side of the dot with the most
    /// room. Wherever it is, the arrow runs from the tag's edge to the ring, so it stays attached as the tag moves.
    /// Returns the layer and the tag's box (for dragging it).
    /// </summary>
    internal static (Control layer, Rect tag) Marker(double cx, double cy, double w, double h, double r, string seller,
        MarkerColours? colours = null, Vector? tagAt = null)
    {
        colours ??= new MarkerColours();
        IBrush ringBrush = Brush(colours.Ring, "#E5332A"), tagBrush = Brush(colours.Tag, "#E5332A"), textBrush = Brush(colours.Text, "#FFFFFF");
        var layer = new Canvas { Width = w, Height = h, ClipToBounds = false };
        // a white halo under the ring keeps it visible on dark and coloured maps alike
        foreach (var (stroke, thick) in new[] { ((IBrush)Brushes.White, 4.5), (ringBrush, 2.5) })
        {
            var ring = new Ellipse { Width = r * 2, Height = r * 2, Stroke = stroke, StrokeThickness = thick };
            Canvas.SetLeft(ring, cx - r); Canvas.SetTop(ring, cy - r);
            layer.Children.Add(ring);
        }
        var name = new TextBlock { Text = string.IsNullOrWhiteSpace(seller) ? "Me" : seller.Trim(), Foreground = textBrush, FontSize = 12, FontWeight = FontWeight.Bold, FontFamily = Font };
        var tag = new Border { Background = tagBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Child = name };
        tag.Measure(Size.Infinity);
        double tw = tag.DesiredSize.Width, th = tag.DesiredSize.Height, gap = 16;
        double tx, ty;
        if (tagAt is { } at)
        {
            // where you put it, kept on the map
            tx = Math.Clamp(cx + at.X - tw / 2, 0, Math.Max(0, w - tw));
            ty = Math.Clamp(cy + at.Y - th / 2, 0, Math.Max(0, h - th));
        }
        else
        {
            // on the side with room: above or below first, then left or right
            bool above = cy - r - gap - th >= 0, below = cy + r + gap + th <= h;
            if (above || below)
            {
                bool up = above && (!below || cy > h / 2);
                tx = Math.Clamp(cx - tw / 2, 0, Math.Max(0, w - tw));
                ty = up ? cy - r - gap - th : cy + r + gap;
            }
            else
            {
                bool left = cx - r - gap - tw >= 0 && (cx + r + gap + tw > w || cx > w / 2);
                tx = left ? cx - r - gap - tw : Math.Min(w - tw, cx + r + gap);
                ty = Math.Clamp(cy - th / 2, 0, Math.Max(0, h - th));
            }
        }
        var box = new Rect(tx, ty, tw, th);
        // The arrow: from where the line to the dot leaves the tag, to the ring. None when the tag sits on the ring.
        var centre = box.Center;
        double dx = cx - centre.X, dy = cy - centre.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len > r + 6)
        {
            double ux = dx / len, uy = dy / len;
            // distance along the line from the tag's centre to its edge
            double tEdge = Math.Min(ux == 0 ? double.MaxValue : tw / 2 / Math.Abs(ux), uy == 0 ? double.MaxValue : th / 2 / Math.Abs(uy));
            var from = new Point(centre.X + ux * tEdge, centre.Y + uy * tEdge);
            var to = new Point(cx - ux * (r + 2), cy - uy * (r + 2));
            if ((to.X - from.X) * ux + (to.Y - from.Y) * uy > 4)   // room for an arrow between them
            {
                foreach (var (stroke, thick) in new[] { ((IBrush)Brushes.White, 4.0), (ringBrush, 2.0) })
                    layer.Children.Add(new Line { StartPoint = from, EndPoint = to, Stroke = stroke, StrokeThickness = thick });
                double hs = 6;
                layer.Children.Add(new Polygon
                {
                    Fill = ringBrush, Stroke = Brushes.White, StrokeThickness = 1,
                    Points = { to, new Point(to.X - ux * hs - uy * hs * 0.7, to.Y - uy * hs + ux * hs * 0.7), new Point(to.X - ux * hs + uy * hs * 0.7, to.Y - uy * hs - ux * hs * 0.7) },
                });
            }
        }
        Canvas.SetLeft(tag, tx); Canvas.SetTop(tag, ty);
        layer.Children.Add(tag);
        return (layer, box);
    }

    /// <summary>A bitmap from 0xAARRGGBB pixels (straight alpha), for icons, portraits and the map.</summary>
    public static Bitmap Bitmap(int[] px, int w, int h)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * w, fb.Address + y * fb.RowBytes, w);
        return bmp;
    }

    /// <summary>Renders the card. Returns its pixels (for the clipboard and PNG) and a bitmap (for the preview).</summary>
    public static (Frame frame, Bitmap bitmap) Render(IReadOnlyList<SaleItem> items, CardOptions o)
    {
        var t = Themes[Math.Clamp(o.Theme, 0, Themes.Length - 1)];
        IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
        IBrush Bg = B(t.Bg), Card = B(t.Card), Tile = B(t.Tile), Text = B(t.Text), Muted = B(t.Muted), Gold = B(t.Accent), Line = B(t.Line),
            BadgeBg = B(t.BadgeBg), BadgeFg = B(t.BadgeFg);
        TextBlock Words(string text, IBrush fg, double size, FontWeight weight = FontWeight.Normal) =>
            new() { Text = text, Foreground = fg, FontSize = size, FontWeight = weight, FontFamily = Font };

        // Who is selling and where: one card that sits in the grid with the items instead of a row of its own.
        var place = new List<string>();
        if (!string.IsNullOrWhiteSpace(o.Server)) place.Add(o.Server.Trim());
        // The channel and the Free Market room stand out as pills of their own.
        string spot = Spot(o.Location), ch = string.IsNullOrWhiteSpace(o.Channel) ? "" : "CH " + o.Channel.Trim();
        bool pills = spot.Length > 0 || ch.Length > 0;
        Border Pill(string text, IBrush back, HorizontalAlignment side) => new()
        {
            Background = back, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 2, 10, 3), HorizontalAlignment = side,
            Child = Words(text, Bg, 15, FontWeight.Bold),
        };
        Control Pills()
        {
            var row = new Grid();
            if (spot.Length > 0 && ch.Length > 0)
            {
                row.Children.Add(Pill(spot, Gold, HorizontalAlignment.Left));
                row.Children.Add(Pill(ch, Text, HorizontalAlignment.Right));
            }
            else row.Children.Add(Pill(spot.Length > 0 ? spot : ch, spot.Length > 0 ? Gold : Text, HorizontalAlignment.Center));
            return row;
        }
        bool hasSeller = !string.IsNullOrWhiteSpace(o.Tagline) || o.Total.Length > 0 || o.Avatar != null || !string.IsNullOrWhiteSpace(o.Seller)
                         || place.Count > 0 || pills || !string.IsNullOrWhiteSpace(o.Location);

        bool hasMap = o.Map != null && hasSeller;
        // Groups by kind: the items arrive sorted by kind; a new kind starts a new group under its own divider.
        static string Kind(SaleItem it) => it.IsWanted ? "Buying" : it.Category == Categories.Unknown ? "Other" : it.Category;
        bool grouped = o.Groups && items.Select(Kind).Distinct().Count() > 1;
        int count = items.Count + (hasSeller ? 1 : 0) + (hasMap ? 2 : 0);
        int cols = count <= 4 ? Math.Max(count, 2) : count <= 12 ? 4 : count <= 30 ? 5 : 6;
        if (hasMap) cols = Math.Max(cols, 4);
        int cardW = items.Any(i => i.IsEquipment) ? 200 : 150;
        const int gap = 10, pad = 22;

        var root = new StackPanel { Background = Bg, Width = pad * 2 + cols * cardW + (cols - 1) * gap };

        if (!string.IsNullOrWhiteSpace(o.Title))
        {
            var title = Words(o.Title.Trim(), Text, 24, FontWeight.Bold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(pad, 16, pad, 0);
            root.Children.Add(title);
        }

        var grid = new Grid { Margin = new Thickness(pad, string.IsNullOrWhiteSpace(o.Title) ? pad : 12, pad - gap, 0), Width = cols * (cardW + gap) };
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(cardW + gap)));
        int cell = 0;
        void Place(Control card)
        {
            // skip the corner the seller card (and the map beside it) sits in
            while ((hasSeller && cell == 0) || (hasMap && cell / cols == 0 && cell % cols >= 1 && cell % cols <= 2)) cell++;
            while (grid.RowDefinitions.Count <= Math.Max(cell / cols, hasMap ? 2 : hasSeller ? 1 : 0)) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(card, cell / cols); Grid.SetColumn(card, cell % cols);
            grid.Children.Add(card);
            cell++;
        }
        Border? sellerBox = null, mapBox = null;   // the cards in the top-left corner, for the packed layout
        double firstRow = 0;   // height of the seller card's row; the tiles beside it share that height
        double Tall(Control e) { e.Measure(new Size(!double.IsNaN(e.Width) && e.Width > 0 ? e.Width : cardW, double.PositiveInfinity)); return e.DesiredSize.Height; }
        if (hasSeller)
        {
            var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (o.Avatar != null)
            {
                var picture = new Image { Source = Bitmap(o.Avatar, AvatarReader.W, AvatarReader.H), Width = AvatarReader.W * 2, Height = AvatarReader.H * 2, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapInterpolationMode(picture, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
                who.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderBrush = Line, BorderThickness = new Thickness(1), Child = picture, HorizontalAlignment = HorizontalAlignment.Center });
            }
            if (!string.IsNullOrWhiteSpace(o.Seller))
            {
                var name = Words(o.Seller.Trim(), Text, 22, FontWeight.Bold);
                name.TextAlignment = TextAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.Margin = new Thickness(0, o.Avatar != null ? 8 : 0, 0, 0);
                who.Children.Add(name);
            }
            if (place.Count > 0)
            {
                var where = Words(string.Join("  •  ", place), Gold, 16, FontWeight.SemiBold);
                where.TextAlignment = TextAlignment.Center; where.TextWrapping = TextWrapping.Wrap; where.Margin = new Thickness(0, 3, 0, 0);
                who.Children.Add(where);
            }
            if (!string.IsNullOrWhiteSpace(o.Tagline))
            {
                var tag = Words(o.Tagline.Trim(), Bg, 12.5, FontWeight.Bold);
                tag.TextAlignment = TextAlignment.Center; tag.TextWrapping = TextWrapping.Wrap;
                who.Children.Add(new Border
                {
                    Background = Gold, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center, Child = tag,
                });
            }
            if (!hasMap && !string.IsNullOrWhiteSpace(o.Location))
            {
                var loc = Words(o.Location.Trim(), Gold, 14);
                loc.TextAlignment = TextAlignment.Center; loc.TextWrapping = TextWrapping.Wrap; loc.Margin = new Thickness(0, 2, 0, 0);
                who.Children.Add(loc);
            }
            if (pills && !hasMap)
            {
                var row = Pills();
                row.Margin = new Thickness(24, 8, 24, 0);
                who.Children.Add(row);
            }
            if (o.Total.Length > 0)
            {
                who.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(30, 10, 30, 8) });
                var head = Words("TOTAL", Muted, 11); head.TextAlignment = TextAlignment.Center; who.Children.Add(head);
                // Split by kind: one line each (the kind on the left, its total on the right), the overall total under them.
                // With the tiles in groups, each kind's total sits on its divider instead, beside the kind's name.
                if (o.KindTotals.Count > 0 && !grouped)
                {
                    var lines = new StackPanel { Margin = new Thickness(4, 4, 4, 2) };
                    foreach (var (kind, amount) in o.KindTotals)
                    {
                        var line = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                        var value = Words(amount, Gold, 13, FontWeight.Bold);
                        value.Margin = new Thickness(8, 0, 0, 0);
                        DockPanel.SetDock(value, Dock.Right);
                        line.Children.Add(value);
                        var label = Words(kind, Text, 12.5);
                        label.TextTrimming = TextTrimming.CharacterEllipsis; label.VerticalAlignment = VerticalAlignment.Center;
                        line.Children.Add(label);
                        lines.Children.Add(line);
                    }
                    who.Children.Add(lines);
                    who.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(30, 4, 30, 4) });
                }
                var sum = Words(o.Total, Gold, 22, FontWeight.Bold); sum.TextAlignment = TextAlignment.Center; who.Children.Add(sum);
                if (o.TotalNote.Length > 0)
                {
                    var note = Words(o.TotalNote, Muted, 11); note.TextAlignment = TextAlignment.Center; note.TextWrapping = TextWrapping.Wrap;
                    who.Children.Add(note);
                }
            }
            var sellerCard = new Border
            {
                CornerRadius = new CornerRadius(10), BorderBrush = Line, BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Margin = new Thickness(0, 0, gap, gap), Child = who,
                // beside the map: same height, character centred
                VerticalAlignment = hasMap ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(sellerCard);
            sellerBox = sellerCard;
            firstRow = Tall(sellerCard);
            if (hasMap)
            {
                // Where you are standing: the minimap's own picture, as wide as two tiles and one row tall.
                var (mpx, mw, mh) = o.Map!.Value;
                double room = (cardW * 2 + gap - 18) * Math.Clamp(o.MapScale, 0.4, 1.0);
                // as wide as its share of two tiles; a tall crop is held to a sensible height (the width shrinks to keep its shape)
                double fit = Math.Min(room / mw, 320 * Math.Clamp(o.MapScale, 0.4, 1.0) / mh);
                double dw = mw * fit, dh = mh * fit;
                var picture = new Image { Source = Bitmap(mpx, mw, mh), Stretch = Stretch.Fill, Width = dw, Height = dh };
                RenderOptions.SetBitmapInterpolationMode(picture, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                var where = new Panel { Width = dw, Height = dh, Children = { picture } };
                if (o.SellerMarker && MapleLite.Core.Game.MapReader.PlayerDot(mpx, mw, mh) is { } dot)
                    where.Children.Add(Marker((dot.x + 0.5) * fit, (dot.y + 0.5) * fit, dw, dh, Math.Max(9, 4.5 * fit), o.Seller, o.MarkerColours,
                        o.MarkerTag is { Length: 2 } tagAt ? new Vector(tagAt[0] * fit, tagAt[1] * fit) : null).layer);
                var mapCard = new Border
                {
                    CornerRadius = new CornerRadius(10), BorderBrush = Line, BorderThickness = new Thickness(1), Background = Card,
                    Padding = new Thickness(4), Margin = new Thickness(0, 0, gap, gap), VerticalAlignment = VerticalAlignment.Stretch,
                };
                var inside = new DockPanel();
                if (!string.IsNullOrWhiteSpace(o.Location) || ch.Length > 0)
                {
                    var heading = new DockPanel { Margin = new Thickness(2, 2, 2, 6) };
                    if (ch.Length > 0)
                    {
                        var channelBadge = new Border
                        {
                            Background = Text, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(8, 0, 0, 0),
                            Child = Words(ch, Bg, 15, FontWeight.Bold),
                        };
                        DockPanel.SetDock(channelBadge, Dock.Right);
                        heading.Children.Add(channelBadge);
                    }
                    var label = Words(o.Location.Trim(), Gold, 14);
                    label.VerticalAlignment = VerticalAlignment.Center; label.TextTrimming = TextTrimming.CharacterEllipsis;
                    heading.Children.Add(label);
                    DockPanel.SetDock(heading, Dock.Top);
                    inside.Children.Add(heading);
                }
                inside.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = where });
                mapCard.Child = inside;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(mapCard, 0); Grid.SetColumn(mapCard, 1); Grid.SetColumnSpan(mapCard, 2);
                grid.Children.Add(mapCard);
                mapBox = mapCard;
                firstRow = Math.Max(firstRow, Tall(mapCard));
            }
        }

        Control Badge(SaleItem it, double size, Thickness padding, double corner, double offset) => new Border
        {
            Background = BadgeBg, CornerRadius = new CornerRadius(corner), Padding = padding,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -offset, -offset),
            Child = Words("×" + it.Qty.Trim(), BadgeFg, size, FontWeight.Bold),
        };
        // Live in your Free Market shop: a green tag on the icon (green in every colour scheme, so it reads the same).
        IBrush ShopGreen = Brush(o.TagColours.Shop, TagColours.ShopDefault);
        bool marked = o.ShopMarker && items.Any(i => i.InShop);
        Border ShopTag(double size) => new()
        {
            Background = ShopGreen, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-5, -5, 0, 0),
            Child = Words("IN SHOP", Brushes.White, size, FontWeight.Bold),
        };
        // Sale or Buying: a pink or blue tag on the icon's top-right corner (the IN SHOP tag has the top-left)
        IBrush SalePink = Brush(o.TagColours.Sale, TagColours.SaleDefault), BuyBlue = Brush(o.TagColours.Buy, TagColours.BuyDefault);
        Border TradeTag(SaleItem it, double size, bool shopTagToo) => new()
        {
            Background = it.Buying ? BuyBlue : SalePink, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1),
            HorizontalAlignment = shopTagToo ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = shopTagToo ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            Margin = shopTagToo ? new Thickness(-5, 0, 0, -5) : new Thickness(0, -5, -5, 0),
            Child = Words(it.Buying ? "BUYING" : "SALE", Brushes.White, size, FontWeight.Bold),
        };
        bool ShowPrice(SaleItem it) => o.Prices && !(o.HideOffer && it.FinalPrice == "Offer");
        // The discount ("-20%") while the price slider is below 100%: on priced items you sell, in the top corner the
        // IN SHOP tag leaves free (top-left, or top-right when IN SHOP has the left and SALE moved down).
        IBrush DiscountOrange = Brush(o.TagColours.Discount, TagColours.DiscountDefault);
        // each tile's own: the card's slider with its kind's and its own adjustment
        string? Discount(SaleItem it) => o.DiscountTags ? DiscountText(o.PriceScale * it.CardScale) : null;
        Border? DiscountTag(SaleItem it, double size, bool shopTagToo) =>
            Discount(it) is not { } discount || it.Buying || !ShowPrice(it) || it.FinalPrice == "Offer" ? null : new Border
            {
                Background = DiscountOrange, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1),
                HorizontalAlignment = shopTagToo ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = shopTagToo ? new Thickness(0, -5, -5, 0) : new Thickness(-5, -5, 0, 0),
                Child = Words(discount, Brushes.White, size, FontWeight.Bold),
            };
        // "ea" where the card holds more than one, so "×2 · 9,999" reads as 9,999 each, not for both
        static string Scaled(SaleItem it, double scale) => Mesos.Scaled(it.FinalPrice, scale);
        string PriceText(SaleItem it) => Scaled(it, o.PriceScale * it.CardScale) + (o.Each && it.FinalPrice != "Offer" && it.Pieces > 1 ? " ea" : "") + (o.Obo && it.FinalPrice != "Offer" ? " OBO" : "");

        // A short tile: the icon on the left, the name and price to its right; equipment stats underneath.
        Control Compact(SaleItem it)
        {
            var icon = new Image { Source = it.Icon, Width = 44, Height = 44, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(icon, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            var tile = new Grid { Width = 54, Height = 54, VerticalAlignment = VerticalAlignment.Center };
            tile.Children.Add(new Border { Background = Tile, CornerRadius = new CornerRadius(7) });
            tile.Children.Add(icon);
            if (it.ShowsQty) tile.Children.Add(Badge(it, 11, new Thickness(4, 0, 4, 1), 5, 5));
            if (marked && it.InShop) tile.Children.Add(ShopTag(8.5));
            if (o.TradeTags) tile.Children.Add(TradeTag(it, 8.5, marked && it.InShop));
            if (DiscountTag(it, 8.5, marked && it.InShop) is { } off85) tile.Children.Add(off85);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var name = Words(it.Name, Text, 12.5);
            name.TextWrapping = TextWrapping.Wrap; name.MaxHeight = 34; name.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(name);
            if (ShowPrice(it))
            {
                var price = Words(PriceText(it), Gold, 15, FontWeight.Bold);
                price.TextTrimming = TextTrimming.CharacterEllipsis; price.Margin = new Thickness(0, 2, 0, 0);
                words.Children.Add(price);
            }
            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tile, Dock.Left);
            row.Children.Add(tile);
            row.Children.Add(words);
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(row);
            if (it.ShowsDetails && o.Stats)
            {
                var details = Words(it.DetailsText, Text, 12);
                details.TextWrapping = TextWrapping.Wrap; details.Margin = new Thickness(0, 8, 0, 0);
                body.Children.Add(details);
            }
            return new Border
            {
                Width = cardW, HorizontalAlignment = HorizontalAlignment.Left, Background = Card, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 10, 8, 10), Margin = new Thickness(0, 0, gap, gap), Child = body,
            };
        }

        var tiles = new List<Control>();
        foreach (var it in items)
        {
            if (o.Compact) { tiles.Add(Compact(it)); continue; }
            var card = new StackPanel();
            var icon = new Image { Source = it.Icon, Width = 64, Height = 64, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(icon, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            var tile = new Grid { Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Center };
            tile.Children.Add(new Border { Background = Tile, CornerRadius = new CornerRadius(8) });
            tile.Children.Add(icon);
            if (it.ShowsQty) tile.Children.Add(Badge(it, 12, new Thickness(5, 1, 5, 2), 6, 6));
            if (marked && it.InShop) tile.Children.Add(ShopTag(9.5));
            if (o.TradeTags) tile.Children.Add(TradeTag(it, 9.5, marked && it.InShop));
            if (DiscountTag(it, 9.5, marked && it.InShop) is { } off95) tile.Children.Add(off95);
            card.Children.Add(tile);
            var name = Words(it.Name, Text, 12.5);
            name.TextAlignment = TextAlignment.Center; name.TextWrapping = TextWrapping.Wrap; name.MaxHeight = 34; name.Height = 34;
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.Margin = new Thickness(0, 10, 0, 0);
            card.Children.Add(name);
            if (ShowPrice(it))
            {
                var price = Words(PriceText(it), Gold, 17, FontWeight.Bold);
                price.TextAlignment = TextAlignment.Center; price.TextTrimming = TextTrimming.CharacterEllipsis;
                card.Children.Add(price);
            }
            if (o.Prices && string.IsNullOrWhiteSpace(it.Custom) && !string.IsNullOrWhiteSpace(it.RateNote))
            {
                var note = Words(it.RateNote, Muted, 11);
                note.TextWrapping = TextWrapping.Wrap; note.TextAlignment = TextAlignment.Center; note.Margin = new Thickness(0, 3, 0, 0);
                card.Children.Add(note);
            }
            if (it.ShowsDetails && o.Stats)
            {
                var details = Words(it.DetailsText, Text, 12);
                details.TextWrapping = TextWrapping.Wrap; details.Margin = new Thickness(0, 8, 0, 2);
                card.Children.Add(details);
            }
            tiles.Add(new Border
            {
                Width = cardW, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = o.Prices ? 168 : 140, Background = Card, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 12, 8, 6), Margin = new Thickness(0, 0, gap, gap), Child = card,
            });
        }
        // A divider: the kind's name in the accent colour and, with totals by kind, the kind's total beside it; then a
        // thin line in the same colour to the right edge.
        Control Divider(string kind, double width)
        {
            var row = new DockPanel { Width = width, Margin = new Thickness(0, 2, 0, gap) };
            var label = Words(kind.ToUpperInvariant(), Gold, 12, FontWeight.Bold);
            label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(2, 0, 10, 0);
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            string kindOf = kind == "Other" ? Categories.Unknown : kind;
            if (o.KindTotals.FirstOrDefault(k => k.Kind == kindOf) is { Total: { Length: > 0 } amount })
            {
                var sum = new Border
                {
                    Background = Gold, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center, Child = Words(amount, Bg, 13, FontWeight.Bold),
                };
                DockPanel.SetDock(sum, Dock.Left);
                row.Children.Add(sum);
            }
            row.Children.Add(new Border { Height = 1.5, Background = Gold, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(1) });
            return row;
        }
        if (o.Pack || grouped)
        {
            // Packed: every tile goes to the top of whichever column is shortest so far (the leftmost of equal ones), at
            // its own height. Tall equipment and short stacks then nest without gaps, and nothing is stretched. The
            // seller card and the map keep the top-left corner; the columns under them start below them.
            // Grouped, each group starts under everything above it, below a divider across the card; the first group's
            // divider sits beside the seller card and map instead, over the columns they leave free.
            var canvas = new Canvas { Margin = grid.Margin, Width = grid.Width };
            var heights = new double[cols];
            double cornerH = firstRow;
            int cornerCols = mapBox != null ? 3 : sellerBox != null ? 1 : 0;
            if (sellerBox != null)
            {
                grid.Children.Remove(sellerBox);
                sellerBox.Height = cornerH - gap; sellerBox.Width = cardW; sellerBox.VerticalAlignment = VerticalAlignment.Top;
                canvas.Children.Add(sellerBox);
                heights[0] = cornerH;
            }
            if (mapBox != null)
            {
                grid.Children.Remove(mapBox);
                mapBox.Height = cornerH - gap; mapBox.Width = cardW * 2 + gap;
                Canvas.SetLeft(mapBox, cardW + gap);
                canvas.Children.Add(mapBox);
                heights[1] = heights[2] = cornerH;
            }
            string? group = null;
            for (int n = 0; n < tiles.Count; n++)
            {
                var tile = tiles[n];
                if (grouped && Kind(items[n]) != group)
                {
                    group = Kind(items[n]);
                    bool besideCorner = n == 0 && cornerCols > 0 && cornerCols < cols;
                    int from = besideCorner ? cornerCols : 0;
                    double top = n == 0 && !besideCorner ? (cornerCols >= cols ? cornerH : 0) : n == 0 ? 0 : heights.Max();
                    var divider = Divider(group, (cols - from) * (cardW + gap) - gap);
                    Canvas.SetLeft(divider, from * (cardW + gap)); Canvas.SetTop(divider, top);
                    canvas.Children.Add(divider);
                    double under = top + Tall(divider) + gap;
                    for (int k = from; k < cols; k++) heights[k] = Math.Max(heights[k], under);
                }
                tile.VerticalAlignment = VerticalAlignment.Top;
                int c = 0;
                for (int k = 1; k < cols; k++) if (heights[k] < heights[c] - 0.5) c = k;
                Canvas.SetLeft(tile, c * (cardW + gap)); Canvas.SetTop(tile, heights[c]);
                canvas.Children.Add(tile);
                heights[c] += Tall(tile);
            }
            canvas.Height = heights.Max();
            root.Children.Add(canvas);
        }
        else
        {
        // Beside the seller card the row is as tall as the card. With standard tiles, each column there holds one
        // (at the bottom of the row when the map is there). With compact tiles, each column takes as many as fit in
        // that height, one above the other; the last one stretches to line up with the bottom of the card.
        int next = 0;
        int besideCard = hasSeller ? cols - 1 - (hasMap ? 2 : 0) : 0;
        for (int c = 0; c < besideCard && next < tiles.Count; c++)
        {
            if (!o.Compact) { if (hasMap) tiles[next].VerticalAlignment = VerticalAlignment.Bottom; Place(tiles[next++]); continue; }
            int fit = 0; double used = 0;
            while (next + fit < tiles.Count && used + Tall(tiles[next + fit]) <= firstRow) { used += Tall(tiles[next + fit]); fit++; }
            if (fit < 2) { Place(tiles[next++]); continue; }
            var stack = tiles.GetRange(next, fit);
            next += fit;
            var column = new DockPanel { LastChildFill = true };
            foreach (var piece in stack)
            {
                if (piece != stack[^1]) { DockPanel.SetDock(piece, Dock.Top); piece.VerticalAlignment = VerticalAlignment.Top; }
                column.Children.Add(piece);
            }
            Place(column);
        }
        while (next < tiles.Count) Place(tiles[next++]);
        root.Children.Add(grid);
        }

        // How to buy: what the tag means, and for everything else, a DM on Discord or a whisper to your IGN.
        if (marked)
        {
            var legend = new WrapPanel { Margin = new Thickness(pad, 4, pad, 0) };
            var tag = ShopTag(10);
            tag.Margin = new Thickness(0, 2, 8, 0);
            legend.Children.Add(tag);
            var live = Words("Live in my Free Market shop now", Text, 13, FontWeight.SemiBold);
            live.Margin = new Thickness(0, 0, 16, 0);
            legend.Children.Add(live);
            if (items.Any(i => !i.InShop))
            {
                string discord = string.IsNullOrWhiteSpace(o.Discord) ? "DM me on Discord" : $"DM {o.Discord.Trim()} on Discord";
                string whisper = string.IsNullOrWhiteSpace(o.Seller) ? "whisper me in game" : $"whisper {o.Seller.Trim()} in game";
                var rest = Words($"Everything else: {discord} or {whisper}", Muted, 13);
                rest.TextWrapping = TextWrapping.Wrap;
                legend.Children.Add(rest);
            }
            root.Children.Add(legend);
        }

        // Footer: when the card was made (UTC, and a second time zone if one is picked), where it was made, price sources.
        var foot = new List<string>();
        var now = o.Utc ?? DateTime.UtcNow;
        if (o.ShowTime)
        {
            string when = now.ToString("MMM d, yyyy  HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
            if (LocalTime(now, o.Zone) is { } local) when += "  /  " + local;
            foot.Add(when);
        }
        if (o.MadeWith) foot.Add("made with MapleLite");
        if (o.Credit.Length > 0) foot.Add(o.Credit);
        if (foot.Count > 0)
        {
            var footer = Words(string.Join("   •   ", foot), Muted, 12);
            footer.Margin = new Thickness(pad, 6, pad, 16); footer.TextWrapping = TextWrapping.Wrap;
            root.Children.Add(footer);
        }
        else root.Children.Add(new Border { Height = pad - gap });

        root.Measure(new Size(root.Width, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        int w = (int)Math.Ceiling(root.DesiredSize.Width), h = (int)Math.Ceiling(root.DesiredSize.Height);
        var rtb = new RenderTargetBitmap(new PixelSize(Math.Max(1, w), Math.Max(1, h)), new Vector(96, 96));
        rtb.Render(root);
        var frame = new Frame(rtb.PixelSize.Width, rtb.PixelSize.Height);
        unsafe
        {
            fixed (int* p = frame.Px)
                rtb.CopyPixels(new PixelRect(rtb.PixelSize), (nint)p, frame.Px.Length * 4, frame.W * 4);
        }
        for (int i = 0; i < frame.Px.Length; i++) frame.Px[i] |= Pixel.Black;   // the card's background fills it: opaque
        return (frame, rtb);
    }
}
