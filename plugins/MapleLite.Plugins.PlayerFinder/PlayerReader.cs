using System.Text.RegularExpressions;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.PlayerFinder;

internal sealed record Sighting(string Name, Region Player, Region? Sign, string? Caption, Region? MapMarker, bool FromBubble);
internal sealed record PlayerScan(Region Scene, Region? Minimap, IReadOnlyList<Sighting> Players,
    IReadOnlyList<Region> Messages, string[]? Nearby);

internal sealed partial class PlayerReader(IOcr ocr)
{
    static readonly OcrPreprocessor LightNames = OcrPreprocessor.BrightText(235);
    const double MapScale = 16;

    public async Task<PlayerScan> ReadAsync(Frame frame, string[] names, string? ownName, Region chat,
        bool highlightSigns, bool markMap, bool highlightChat, CancellationToken ct)
    {
        var scene = Nameplates.Scene(frame);
        var map = MapReader.PictureBounds(frame);
        var minimap = map is { } picture ? new Region(0, 0, picture.Right + 30, picture.Bottom + 18).Intersect(scene) : (Region?)null;
        var signs = FindShopSigns(frame, scene);
        var request = new OcrRequest(scene) { Scale = 2, Scaling = OcrScaling.Bicubic };
        var plain = await ocr.TryReadAsync(frame, request, ct).ConfigureAwait(false);
        var bubbles = SpeechBubbles.Find(frame, plain, chat, minimap);
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (markMap && names.Length > 0 && !string.IsNullOrEmpty(ownName)) wanted.Add(ownName);
        var found = MatchNames(frame, plain, wanted, minimap, chat, signs, bubbles, discover: true);
        if (wanted.Any(name => !found.ContainsKey(name) && !bubbles.Any(bubble => bubble.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
        {
            var bright = await ocr.TryReadAsync(frame, request with { Preprocess = LightNames }, ct).ConfigureAwait(false);
            foreach (var (name, box) in MatchNames(frame, bright, wanted, minimap, chat, signs, bubbles, discover: false))
                found.TryAdd(name, box);
        }

        (double x, double y)? selfDot = null;
        if (markMap && map is { } area && ownName != null && found.ContainsKey(ownName))
        {
            var image = frame.Crop(area);
            selfDot = MapReader.PlayerDot(image.Px, image.W, image.H);
        }

        var players = new List<Sighting>();
        foreach (string name in names)
        {
            var speech = bubbles.FirstOrDefault(bubble => bubble.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool hasPlate = found.TryGetValue(name, out var plate);
            if (!hasPlate && speech == null) continue;
            if (speech is { } bubble && (!hasPlate || Math.Abs(plate.CenterX - bubble.TailX) > 40
                || plate.Top - bubble.Box.Bottom is < 16 or > 135))
            {
                hasPlate = false;
                // When tags overlap, the tail still points to the speaker. Estimate the
                // avatar's vertical position using the normal distance from the tail to the feet.
                plate = new TextBounds(bubble.TailX - 22, bubble.Box.Bottom + 76, 44, 11);
            }
            var player = Nameplates.CharacterAbove(plate, scene);
            if (player.IsEmpty) continue;
            ShopSign? shop = highlightSigns && hasPlate && speech == null ? NearestSign(signs, plate, found.Values) : null;
            var sign = highlightSigns ? speech?.Box ?? shop?.Outline : null;
            string? caption = sign == null ? null : ReadCaption(plain, shop?.Caption ?? sign.Value);
            Region? marker = null;
            if (hasPlate && selfDot is { } dot && map is { } mapArea && ownName != null && found.TryGetValue(ownName, out var own))
                marker = ProjectMarker(frame, mapArea, dot, own, plate, name.Equals(ownName, StringComparison.OrdinalIgnoreCase));
            players.Add(new Sighting(name, player, sign, caption, marker, !hasPlate));
        }
        var nearby = found.Keys.Concat(bubbles.Select(bubble => bubble.Name))
            .Where(name => !name.Equals(ownName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new PlayerScan(scene, minimap, players, highlightChat ? FindMessages(plain, names, chat) : [],
            plain.Count == 0 && nearby.Length == 0 ? null : nearby);
    }

    static IReadOnlyList<Region> FindMessages(IReadOnlyList<OcrLine> lines, string[] names, Region chat)
    {
        var words = lines.SelectMany(line => line.Words)
            .Where(word => chat.Contains((int)word.Box.CenterX, (int)word.Box.CenterY)).ToList();
        var rows = OcrLayout.GroupRows([OcrLine.FromWords(words)]);
        var messages = new List<Region>();
        foreach (var row in rows)
        {
            var match = ChatSender().Match(row.Text);
            if (!match.Success || !names.Contains(match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)) continue;
            int x = (int)Math.Floor(row.Box.Left) - 2, y = (int)Math.Floor(row.Box.Top) - 1;
            messages.Add(new Region(x, y, (int)Math.Ceiling(row.Box.Right) - x + 2,
                (int)Math.Ceiling(row.Box.Bottom) - y + 1).Intersect(chat));
        }
        return messages;
    }

    [GeneratedRegex(@"^\[\s*\d{1,2}\s*:\s*\d{2}\s*\]\s*(?:\[(?:Buddy|Party|Guild|Alliance|Whisper|General)\]\s*)?(?:\[(?:From|To)\s+(?<name>[A-Za-z0-9_]{2,16})\]\s*:?|(?<name>[A-Za-z0-9_]{2,16})\s*[:：>])\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex ChatSender();

    internal static bool IsName(string name) => Nameplates.IsName(name);

    static Dictionary<string, TextBounds> MatchNames(Frame frame, IReadOnlyList<OcrLine> lines, HashSet<string> wanted,
        Region? minimap, Region chat, IReadOnlyList<ShopSign> signs, IReadOnlyList<SpeechBubble> bubbles, bool discover)
    {
        var matches = new Dictionary<string, TextBounds>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var words = line.Words.Count == 0 ? [new OcrWord(line.Text, line.Box)] : line.Words.OrderBy(word => word.Box.X).ToArray();
            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i];
                Take(word.Text, word.Box, discover);
                // Narrow gaps can split one name into several OCR words. Keep the whole token and require a height match.
                if (i + 1 < words.Length)
                {
                    var next = words[i + 1];
                    if (next.Box.Left - word.Box.Right is >= 0 and <= 2.5 && Math.Abs(next.Box.CenterY - word.Box.CenterY) <= 2)
                        Take(word.Text + next.Text, TextBounds.Union([word.Box, next.Box]), false);
                }
            }
        }
        return matches;

        void Take(string text, TextBounds box, bool allowDiscovery)
        {
            text = text.Trim('•', '.', ':', '\'', '"', ',', '|');
            if (box.Height is < 5 or > 18 || (!wanted.Contains(text) && (!allowDiscovery || !IsName(text)))) return;
            int x = (int)box.CenterX, y = (int)box.CenterY;
            if (minimap?.Contains(x, y) == true || chat.Contains(x, y)
                || signs.Any(sign => sign.Outline.Contains(x, y)) || bubbles.Any(bubble => bubble.Box.Contains(x, y))) return;
            if (!LooksLikeNameplate(frame, box)) return;
            matches.TryAdd(text, box);
        }
    }

    static bool LooksLikeNameplate(Frame frame, TextBounds box) => Nameplates.LooksLikeNameplate(frame, box);

    sealed record ShopSign(Region Outline, Region Caption);

    static List<ShopSign> FindShopSigns(Frame frame, Region scene)
    {
        var signs = new List<ShopSign>();
        // Classic World shop signs have a 103/105-pixel white rule between the caption and controls.
        for (int y = 3; y < scene.Bottom - 15; y++)
        {
            int row = y * frame.W;
            for (int x = 0; x < frame.W;)
            {
                int left = x++;
                if (!White(frame.Px[row + left])) continue;
                while (x < frame.W && White(frame.Px[row + x])) x++;
                int width = x - left;
                if (width is < 100 or > 108) continue;
                int pale = 0;
                for (int xx = left; xx < x; xx++)
                    if ((frame.At(xx, y - 1) & 0xffffff) is 0xeeeeff or 0xffffee or 0xffffdd) pale++;
                if (pale * 4 < width * 3) continue;
                // The right-hand pill has white pixels at both ends, seven rows below the divider.
                if (!White(frame.At(x - 8, y + 7)) || !White(frame.At(x - 45, y + 7))) continue;
                int top = Math.Max(0, y - 36);
                signs.Add(new ShopSign(
                    new Region(left - 3, top, width + 6, y - top + 22).Intersect(scene),
                    new Region(left, top, width, y - top)));
            }
        }
        return signs;

        static bool White(int pixel) => (pixel & 0xffffff) == 0xffffff;
    }

    static ShopSign? NearestSign(IEnumerable<ShopSign> signs, TextBounds plate, IEnumerable<TextBounds> nameplates)
    {
        static double Score(ShopSign sign, TextBounds box)
        {
            double dx = Math.Abs(box.CenterX - (sign.Caption.X + sign.Caption.Width / 2.0));
            double dy = Math.Abs(box.Top - sign.Caption.Bottom - 98);
            return dx <= 29 && dy <= 16 ? dx + dy * 2 : double.PositiveInfinity;
        }

        var candidates = signs.Select(sign => (sign, score: Score(sign, plate)))
            .Where(item => double.IsFinite(item.score)).OrderBy(item => item.score).ToList();
        if (candidates.Count == 0) return null;
        var best = candidates[0];
        if (candidates.Count > 1 && candidates[1].score - best.score < 8) return null;
        return nameplates.Any(other => other != plate && Score(best.sign, other) <= best.score + 4) ? null : best.sign;
    }

    static string? ReadCaption(IReadOnlyList<OcrLine> lines, Region area)
    {
        var rows = lines.Where(line => area.Contains((int)line.Box.CenterX, (int)line.Box.CenterY))
            .OrderBy(line => line.Box.Top).ThenBy(line => line.Box.Left).Select(line => line.Text);
        var caption = string.Join(" ", rows);
        return caption.Length == 0 ? null : caption;
    }

    static Region? ProjectMarker(Frame f, Region map, (double x, double y) selfDot,
        TextBounds own, TextBounds target, bool isOwn)
    {
        double x = map.X + selfDot.x + (target.CenterX - own.CenterX) / MapScale;
        double y = map.Y + selfDot.y + (target.Top - own.Top) / MapScale;
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        if (isOwn) return new Region(cx - 5, cy - 5, 11, 11).Intersect(map);

        // Require the game's red dot pattern near the projected point. If two dots fit almost equally, skip it.
        var dots = new List<(int X, int Y, double Distance)>();
        for (int yy = Math.Max(map.Y + 1, cy - 2); yy <= Math.Min(map.Bottom - 2, cy + 2); yy++)
            for (int xx = Math.Max(map.X + 1, cx - 2); xx <= Math.Min(map.Right - 2, cx + 2); xx++)
                if (Red(xx, yy, 0xdd0000) && Red(xx - 1, yy, 0xee0000) && Red(xx + 1, yy, 0xee0000)
                    && Red(xx, yy - 1, 0xee0000) && Red(xx, yy + 1, 0xee0000))
                    dots.Add((xx, yy, Math.Sqrt((xx - x) * (xx - x) + (yy - y) * (yy - y))));
        var nearby = dots.OrderBy(dot => dot.Distance).ToArray();
        if (nearby.Length == 0 || nearby[0].Distance > 2.2
            || (nearby.Length > 1 && nearby[1].Distance - nearby[0].Distance < 1)) return null;
        return new Region(nearby[0].X - 5, nearby[0].Y - 5, 11, 11).Intersect(map);

        bool Red(int xx, int yy, int color) => (f.At(xx, yy) & 0xffffff) == color;
    }
}
