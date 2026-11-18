using System.Runtime.InteropServices;
using MapleLite.Core.Imaging;
using MapleLite.Core.Platform;

namespace MapleLite.Platform.Windows;

/// <summary>
/// Puts a picture on the Windows clipboard in two forms: "PNG" (Discord, browsers and Office keep transparency)
/// and CF_DIB (everything else, such as Paint).
/// </summary>
internal sealed class WindowsClipboard : IClipboardImage
{
    const uint CF_DIB = 8, GMEM_MOVEABLE = 0x0002;
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint h);

    public async Task SetImageAsync(Frame image, byte[] png)
    {
        // Another program may hold the clipboard for a moment: retry briefly.
        for (int i = 0; !OpenClipboard(0); i++)
        {
            if (i >= 10) throw new InvalidOperationException("The clipboard is busy in another program. Try again.");
            await Task.Delay(50).ConfigureAwait(false);
        }
        try
        {
            EmptyClipboard();
            Put(RegisterClipboardFormat("PNG"), png);
            Put(CF_DIB, Dib(image));
        }
        finally { CloseClipboard(); }
    }

    static void Put(uint format, byte[] bytes)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (h == 0) throw new OutOfMemoryException("Couldn't reserve clipboard memory.");
        var p = GlobalLock(h);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);
        // On success the clipboard owns the memory; free it only when it was refused.
        if (SetClipboardData(format, h) == 0) GlobalFree(h);
    }

    /// <summary>A 32-bit bottom-up DIB (BITMAPINFOHEADER + pixels), composited over white for apps that ignore alpha.</summary>
    internal static byte[] Dib(Frame f)
    {
        const int header = 40;
        var bytes = new byte[header + f.W * f.H * 4];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), header);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), f.W);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), f.H);          // positive height: bottom-up rows
        BitConverter.TryWriteBytes(bytes.AsSpan(12), (short)1);    // planes
        BitConverter.TryWriteBytes(bytes.AsSpan(14), (short)32);   // bits per pixel
        BitConverter.TryWriteBytes(bytes.AsSpan(20), f.W * f.H * 4);
        int o = header;
        for (int y = f.H - 1; y >= 0; y--)
            for (int x = 0; x < f.W; x++)
            {
                int p = f.Px[y * f.W + x], a = (p >> 24) & 255;
                int Blend(int c) => (c * a + 255 * (255 - a)) / 255;
                bytes[o++] = (byte)Blend(p & 255);
                bytes[o++] = (byte)Blend((p >> 8) & 255);
                bytes[o++] = (byte)Blend((p >> 16) & 255);
                bytes[o++] = 255;
            }
        return bytes;
    }
}
