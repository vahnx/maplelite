using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>A character found by its name tag: the name (as the user wrote it, for NPCs), the tag, and the box the character stands in above it.</summary>
public sealed record TagSighting(string Name, TextBounds Plate, Region Body);

/// <summary>One scene read: the listed NPCs found, and the names of every NPC in view.</summary>
public sealed record NpcScan(List<TagSighting> Found, List<string> InView);

/// <summary>
/// Finding NPCs by name. NPC tags are yellow lettering on the same dark backing as player tags (whose lettering is
/// white), and NPC names can be several words. The scene is read with only yellow lettering kept, so a player's tag
/// standing against an NPC's can't merge with it. Between reads, <see cref="NameTagTracker"/> follows the tags.
/// </summary>
public static class NpcTags
{
    /// <summary>
    /// A name squeezed to lowercase letters and digits for comparing with OCR text ("Mr. Pickall" → "mrpickall"),
    /// with the letters OCR mixes up folded together (i, l, 1 and | read as l; 0 as o).
    /// </summary>
    public static string Compact(string text) =>
        new(GameText.FoldOcrLetters(text).Where(c => char.IsLetterOrDigit(c) || c == '|').Select(c => char.ToLowerInvariant(c) switch
        {
            'i' or '1' or '|' or '!' => 'l',
            '0' => 'o',
            var x => x,
        }).ToArray());

    /// <summary>True for yellow lettering (anti-aliased yellow included); white and grey are not.</summary>
    public static bool IsYellow(int p) => OcrPreprocessor.IsYellow(p);

    /// <summary>
    /// True when the text sits on an NPC name tag: mostly dark backing (as <see cref="Nameplates.LooksLikeNameplate"/>)
    /// with yellow lettering that outnumbers any white.
    /// </summary>
    public static bool LooksLikeNpcTag(Frame frame, TextBounds box)
    {
        var bounds = new Region((int)Math.Floor(box.Left), (int)Math.Floor(box.Top),
            (int)Math.Ceiling(box.Width), (int)Math.Ceiling(box.Height)).Intersect(frame.Bounds);
        if (bounds.IsEmpty) return false;
        int dark = 0, yellow = 0, white = 0;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                int p = frame.At(x, y);
                if (Pixel.Max(p) <= 95) dark++;
                else if (IsYellow(p)) yellow++;
                else if (Pixel.Min(p) >= 200) white++;
            }
        int total = bounds.Width * bounds.Height;
        return dark >= total * .45 && yellow >= Math.Max(6, total * .06) && yellow > white;
    }

    /// <summary>
    /// True when OCR text matches a wanted name, ignoring case, spaces and punctuation (both already
    /// <see cref="Compact"/>ed). Names of 4+ letters allow one misread letter, 10+ letters two.
    /// </summary>
    public static bool Matches(string ocrCompact, string wantCompact)
    {
        if (ocrCompact.Length == 0 || wantCompact.Length == 0) return false;
        if (ocrCompact == wantCompact) return true;
        int allowed = wantCompact.Length >= 10 ? 2 : wantCompact.Length >= 4 ? 1 : 0;
        return allowed > 0 && Math.Abs(ocrCompact.Length - wantCompact.Length) <= allowed && Distance(ocrCompact, wantCompact) <= allowed;
    }

    /// <summary>The scene read NPC Highlighter makes: yellow lettering only, enlarged twice.</summary>
    public static OcrRequest Request(Frame frame) =>
        new(Nameplates.Scene(frame)) { Scale = 2, Scaling = OcrScaling.Bicubic, Preprocess = OcrPreprocessor.YellowText };

    /// <summary>
    /// Every listed NPC visible in the game world, from one read with only yellow lettering kept.
    /// </summary>
    public static async Task<List<TagSighting>> FindAsync(IOcr ocr, Frame frame, IReadOnlyList<string> names, CancellationToken ct = default) =>
        names.Count == 0 ? [] : (await ScanAsync(ocr, frame, names, false, ct).ConfigureAwait(false)).Found;

    /// <summary>
    /// <see cref="FindAsync"/>, and with <paramref name="listInView"/> also the name of every NPC in view (from
    /// the same read, so it costs no extra OCR), leaving out the chat panel and NPC titles.
    /// </summary>
    public static async Task<NpcScan> ScanAsync(IOcr ocr, Frame frame, IReadOnlyList<string> names, bool listInView, CancellationToken ct = default)
    {
        var request = Request(frame);
        var lines = await ocr.TryReadAsync(frame, request, ct).ConfigureAwait(false);
        // One read only: keeping just the yellow lettering already reads tags on any background, and a second,
        // plain read for names not in view would double the OCR load of every scan (slowing other plugins' reads).
        var found = Match(frame, lines, names);
        return new NpcScan(found.OrderBy(f => f.Plate.X).ToList(), listInView ? InView(frame, lines, ChatPanel.Find(frame)) : []);
    }

    /// <summary>
    /// The text of every NPC name tag among OCR lines of the yellow-only read: words standing together on a tag,
    /// outside the chat panel, without bracketed notices. A tag right under another is that NPC's title
    /// ("Blacksmith" under "Silas Irons") and is left out.
    /// </summary>
    public static List<string> InView(Frame frame, IReadOnlyList<OcrLine> lines, Region? chat)
    {
        var tags = new List<(string Text, TextBounds Box)>();
        foreach (var line in lines)
        {
            var words = line.Words.Count == 0 ? [new OcrWord(line.Text, line.Box)] : line.Words.OrderBy(w => w.Box.X).ToArray();
            int start = 0;
            for (int i = 1; i <= words.Length; i++)
            {
                if (i < words.Length && words[i].Box.Left - words[i - 1].Box.Right <= Math.Max(8, words[i].Box.Height * .9)
                    && Math.Abs(words[i].Box.CenterY - words[i - 1].Box.CenterY) <= 3) continue;
                var group = words[start..i];
                start = i;
                var text = string.Join(' ', group.Select(w => w.Text)).Trim('•', '.', ':', '\'', '"', ',', '|', ' ');
                var box = TextBounds.Union(group.Select(w => w.Box));
                if (text.Length is < 2 or > 24 || text.IndexOfAny(['[', ']', ':', '<', '>']) >= 0 || Compact(text).Length < 2) continue;
                if (box.Height is < 5 or > 18 || chat is { } c && c.Contains((int)box.CenterX, (int)box.CenterY)) continue;
                if (LooksLikeNpcTag(frame, box)) tags.Add((text, box));
            }
        }
        return tags.Where(t => !tags.Any(above => above.Box.Bottom <= t.Box.Top + 1 && t.Box.Top - above.Box.Bottom <= 12
                && above.Box.Left < t.Box.Right && t.Box.Left < above.Box.Right))
            .Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Finds the wanted names among OCR lines of <paramref name="frame"/>'s scene, keeping those on NPC tags.</summary>
    public static List<TagSighting> Match(Frame frame, IReadOnlyList<OcrLine> lines, IReadOnlyList<string> names)
    {
        var scene = Nameplates.Scene(frame);
        var wanted = names.Select(n => (Name: n, Key: Compact(n), Words: Math.Max(1, n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)))
            .Where(w => w.Key.Length > 0).ToList();
        var found = new List<TagSighting>();
        foreach (var line in lines)
        {
            var words = line.Words.Count == 0 ? [new OcrWord(line.Text, line.Box)] : line.Words.OrderBy(w => w.Box.X).ToArray();
            for (int start = 0; start < words.Length; start++)
            {
                string text = "";
                var box = words[start].Box;
                for (int end = start; end < words.Length && end - start < 6; end++)
                {
                    if (end > start)
                    {
                        // Words of one tag sit on one row with at most a space between them.
                        var gap = words[end].Box.Left - words[end - 1].Box.Right;
                        if (gap > Math.Max(8, words[end].Box.Height * .9) || Math.Abs(words[end].Box.CenterY - words[end - 1].Box.CenterY) > 3) break;
                        box = TextBounds.Union([box, words[end].Box]);
                    }
                    text += Compact(words[end].Text);
                    foreach (var want in wanted)
                    {
                        if (end - start + 1 > want.Words + 1) continue;
                        TextBounds plate;
                        if (Matches(text, want.Key)) plate = box;
                        else if (text.Length > want.Key.Length && text.IndexOf(want.Key, StringComparison.Ordinal) is var at and >= 0)
                            // Two tags read as one word: keep the share of the box the name takes.
                            plate = new TextBounds(box.X + box.Width * at / text.Length, box.Y, box.Width * want.Key.Length / text.Length, box.Height);
                        else continue;
                        if (plate.Height is < 5 or > 18 || found.Any(f => Overlaps(f.Plate, plate)) || !LooksLikeNpcTag(frame, plate)) continue;
                        var body = Nameplates.CharacterAbove(plate, scene);
                        if (!body.IsEmpty) found.Add(new TagSighting(want.Name, plate, body));
                    }
                }
            }
        }
        return found;
    }

    internal static bool Overlaps(TextBounds a, TextBounds b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
