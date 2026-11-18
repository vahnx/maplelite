using MapleLite.Core.Imaging;
using MapleLite.Core.Platform;
using static MapleLite.Platform.Mac.Interop.ObjC;

namespace MapleLite.Platform.Mac;

/// <summary>Puts a picture on the general pasteboard as PNG, which Discord, browsers, Preview and Messages all accept.</summary>
internal sealed unsafe class MacClipboard : IClipboardImage
{
    public Task SetImageAsync(Frame image, byte[] png)
    {
        Load(AppKit);
        Pool(() =>
        {
            var board = Send(Class("NSPasteboard"), "generalPasteboard");
            Send(board, "clearContents");
            nint data;
            fixed (byte* p = png) data = Send(Class("NSData"), "dataWithBytes:length:", (nint)p, png.Length);
            if (!IsYes(Send(board, "setData:forType:", data, Str("public.png"))))
                throw new InvalidOperationException("Couldn't put the picture on the clipboard.");
            return 0;
        });
        return Task.CompletedTask;
    }
}
