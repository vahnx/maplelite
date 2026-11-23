using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.PlayerFinder;

internal static class ChatPanel
{
    // The Classic World chat toggle is anchored to the centered HUD. Its + or − indicates
    // the current state even if Chat History was configured at a different panel height.
    public static Region? Find(Frame frame)
    {
        if (frame.W < 800 || frame.H < 230) return null;
        int hudLeft = (frame.W - 800) / 2;
        int button = hudLeft + 535, glyph = frame.H - 52, input = frame.H - 68;
        if (Rgb(frame, button, glyph) != 0 || Rgb(frame, button + 11, glyph) != 0
            || Rgb(frame, button + 5, glyph) != 0xddffff) return null;

        int left = hudLeft - 1, right = hudLeft + 567, bottom = input + 31;
        if (Rgb(frame, button + 3, glyph) != 0xddffff)
            return BoundsAt(input);

        // Find the head of the live scrollbar. It moves vertically along with the chat tabs.
        int rail = hudLeft + 551;
        int top = input - 1;
        while (top > 20 && Rgb(frame, rail, top) == 0xdddddd
            && Rgb(frame, rail + 14, top) == 0xdddddd) top--;
        if (input - top < 14)
        {
            // Rounded arrow buttons have an inset border on their first and last rows.
            for (int y = input - 20; y > Math.Max(15, input - 450); y--)
                if (Rgb(frame, rail + 3, y) == 0x8899aa && Rgb(frame, rail + 11, y) == 0x8899aa
                    && Rgb(frame, rail + 2, y + 2) == 0xeeeeff && Rgb(frame, rail + 12, y + 2) == 0xeeeeff)
                    return BoundsAt(y - 17);
            return null;
        }
        return BoundsAt(Math.Max(0, top - 15));

        Region BoundsAt(int y) => new(left, y, right - left, bottom - y);
    }

    static int Rgb(Frame frame, int x, int y) => frame.At(x, y) & 0xffffff;
}
