# MapleLite

A plugin host for **MapleStory Classic World**. It runs on **Windows, macOS
and Linux** from one codebase (.NET 8 + Avalonia).

MapleLite is **passive**. It reads the game only from pictures of its window. It never injects code,
reads game memory, captures packets, or sends input to the game, and neither can its plugins.

## Projects

| Project | What it is |
|---|---|
| `src/MapleLite.Core` | No UI, no OS calls. Frames, the **shared OCR service**, the capture loop, platform interfaces. |
| `src/MapleLite.Sdk` | The plugin API (`IPlugin`, `IPluginContext`), theme tokens, themed panel helpers. Plugins reference only this. |
| `src/MapleLite.Platform.Windows` | Window finding + mode detection, Windows.Graphics.Capture, Windows OCR. |
| `src/MapleLite.Platform.Mac` | macOS (milestone 5): ScreenCaptureKit, Apple Vision OCR. Stub today. |
| `src/MapleLite.Platform.Linux` | Linux (milestone 5): X11/XComposite for the Wine/Proton window, Tesseract OCR. Stub today. |
| `src/MapleLite.Host` | The Avalonia app: Game page, plugin pages, Settings, themes, plugin loader. |
| `plugins/*` | Bundled plugins: Chat History (the MapleMemory app, ported), Loot, Sale Card, XP Tracker. |
| `samples/HelloPlugin` | A minimal third-party plugin to copy. |
| `tests/MapleLite.Tests` | xUnit tests on fake engines and synthetic frames (never the live game). |

## Shared OCR

Platforms provide only a raw recognizer (`IOcrEngine`). Everything else lives once in Core's `OcrService`,
which plugins get as `context.Ocr`:

- crop, cleanup (`OcrPreprocessor`: `ChatGlyphs`, `BrightText`, `Invert`, `Grayscale`, or your own),
  enlarge (auto up to 3×, smooth or nearest, optional padding), recognize, map boxes back to frame coordinates
- optional regrouping of words into on-screen rows (`OcrGrouping.Rows`)
- **sharing**: the capture loop gives every plugin the same frame object, so identical reads of the same frame
  run once, even across plugins
- a concurrency cap so OCR can't starve capture

```csharp
var r = await context.Ocr.ReadAsync(frame, new OcrRequest(chatRegion)
{
    Preprocess = OcrPreprocessor.ChatGlyphs,
    Grouping = OcrGrouping.Rows,
});
foreach (var line in r.Lines) Console.WriteLine($"{line.Box.Y:0}: {line.Text}");
```

## Themes

Seven built-in themes: Midnight, Daybreak, Maple, Forest, Ocean, Sakura, High contrast. Every color is a token
(`ml.Background`, `ml.Surface`, `ml.Accent`, …) published as an app resource. Stock controls are mapped onto
the tokens too, so the host and every plugin page restyle live. Users add themes as JSON in the themes folder
(Settings → *Copy to a file to edit*). A theme may set only some tokens and inherit the rest from a base.

## Overlays

Not built, off by default. See [docs/OVERLAYS.md](docs/OVERLAYS.md) for the safety rules (never inside the game
process, display-only and click-through, unavailable in exclusive fullscreen) and the decisions still open.

## Build and run

You need the free [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows, macOS or Linux) and this repo.
Run the commands in a terminal in the repo folder.

### Windows

```bash
dotnet build
dotnet test
dotnet run --project src/MapleLite.Host -f net8.0-windows10.0.19041.0
```

Build output goes outside the source tree, to `%LOCALAPPDATA%\MapleLite-build` (this folder is synced by Google Drive,
which locks freshly written build files). After a build you can also start the app directly from
`%LOCALAPPDATA%\MapleLite-build\bin\MapleLite.Host\debug_net8.0-windows10.0.19041.0\MapleLite.exe`.

To make a standalone copy that runs on any 64-bit Windows 10/11 PC without .NET installed:

```bash
dotnet publish src/MapleLite.Host -f net8.0-windows10.0.19041.0 -r win-x64 -c Release --self-contained -o publish/win
```

Then run `publish\win\MapleLite.exe`, or zip the `publish\win` folder to share it (about 120 MB, plugins included).
If the repo sits in a synced folder (Google Drive, OneDrive), publish outside it, for example
`-o %LOCALAPPDATA%\MapleLite-publish\win`, so the sync client doesn't lock or upload the 120 MB of files.

### macOS

Build on the Mac itself:

```bash
dotnet build
dotnet run --project src/MapleLite.Host -f net8.0
```

Standalone copy (use `osx-x64` instead of `osx-arm64` for Intel Macs):

```bash
dotnet publish src/MapleLite.Host -f net8.0 -r osx-arm64 -c Release --self-contained -o publish/mac
```

Run it with `./publish/mac/MapleLite`. If the folder was built on Windows and copied over, first run
`chmod +x publish/mac/MapleLite` (Windows doesn't keep the "executable" flag). A copy downloaded from the internet is
blocked by Gatekeeper until you run `xattr -dr com.apple.quarantine publish/mac`, because it isn't signed yet.
A proper double-clickable, signed `MapleLite.app` comes with the macOS milestone.

**Status on macOS:** the window, themes, plugins and Settings all run, but the macOS capture and OCR layer isn't built
yet (milestone 5), so it can't see the game. To try everything with a saved screenshot:
`./publish/mac/MapleLite --replay path/to/capture.png` (OCR needs the macOS layer, so text reading stays off).

### Linux

Same as macOS with `-r linux-x64`; same status (the Linux capture and OCR layer is milestone 5).

### Test switches

Run the built `MapleLite` executable with these. None of them touch the live game:

- `--replay <png>`: run the whole host on a saved capture instead of the game
- `--ocrtest <png> --out <txt> [--region x,y,w,h]`: read a saved capture with the shared OCR and write the lines
  (with `--region`: chat-glyph cleanup, compared across enlarge modes)
- `--snapshot <png> [--theme <id>] [--page game|settings|<plugin-id>] [--wait <s>]`: render the window to a PNG and exit
- `--install <zip|dll>` / `--uninstall <plugin-id>`: add or remove a plugin at startup

Set `MAPLELITE_DATA` to a scratch folder to keep test runs away from your real settings.

## Data

`%LOCALAPPDATA%\MapleLite` on Windows, `~/Library/Application Support/MapleLite` on macOS,
`~/.local/share/MapleLite` on Linux: `settings.json`, `themes/`, `plugins/<id>/`, `maplelite.log`.

## Writing a plugin

Reference `MapleLite.Sdk`, implement `IPlugin`, and build to `plugins/<Name>/<Name>.dll`. Each plugin loads in
its own `AssemblyLoadContext`; Core, Sdk and Avalonia come from the host. Use `context.Frames.Subscribe` for
frames (keep the callback short), `context.Ocr` for text, `context.Storage` for data, and the `PanelKit`
helpers or `ml.*` resources for a page that follows the theme. Third-party plugins will come through a
reviewed hub that builds them from source.

## Chat History (formerly MapleMemory)

The chat journal from the MapleMemory app, as a plugin. Its parsers, spam and partial-repeat filters and time filters
are the app's code, unchanged (the loot tracker is now the Loot plugin); its 99 self-test checks run as xUnit tests. What changed: frames come
from the host's capture loop and text from the shared OCR (chat-glyph cleanup, row grouping, bicubic enlarge), and
the page is Avalonia, so it runs on every OS the host does.

- Journal format is unchanged: `messages-YYYY-MM.jsonl` and `settings.json`, now in `plugins/chathistory/` under the
  MapleLite data folder.
- On first start it **copies** the MapleMemory app's history and chat area from `%LOCALAPPDATA%\MapleMemory`
  (the app's files are never changed). "Import from MapleMemory app" copies any lines added since, without duplicates.
- Checked against the app on a saved capture: the same three messages, channels (Megaphone, Buddy, Megaphone) and
  wrapped-line assembly.

## Loot

Totals of the mesos and items you pick up, by session, day, week or all time, with a CSV export. Moved out of Chat History
into its own plugin. Pickups come from two places:

- the game's pickup notices in the lower-right of the picture, read by the Loot plugin with the shared OCR;
- pickup lines printed in chat, which Chat History publishes on the event bus (when Chat History is on).

Chat History also publishes where the chat box is, and Loot leaves that area out of its own reads so the same pickup
isn't counted twice. Pickups are saved as `loot-YYYY-MM.jsonl` in `plugins/loot/`. First start copies the pickups out
of the MapleMemory app's history.

## Managing plugins

**Settings → Plugins** lists every plugin with an on/off switch. Switching takes effect at once: off stops the plugin
and removes its page (MapleLite also drops any frame or event subscriptions it left behind); on starts a fresh copy.
The choice is remembered.

- **Add plugin…** installs a plugin from a `.zip` (or a single `.dll`) after a warning: plugins run with your access and
  can read your chat, so only add ones you trust. The file is checked first (it must contain a plugin for this API
  version with an id that isn't taken). Added plugins live in `installed-plugins/` in the data folder.
- **Remove** deletes an added plugin's files right away (plugins are loaded from memory, so their files are never
  locked). Its saved data stays in `plugins/<id>/`. Bundled plugins can be switched off but not removed.
- Scripts can do the same: `MapleLite --install <zip|dll>` and `MapleLite --uninstall <plugin-id>`.

`samples/HelloPlugin` is a minimal plugin to copy: it reads text with the shared OCR and listens to chat events.

### Do plugin .dll files work on macOS and Linux?

Yes. A plugin `.dll` built for plain `net8.0` holds .NET bytecode (IL), not Windows machine code. The .NET runtime on
each OS compiles it to native code as it runs, so **the same file works on Windows, macOS (Intel and Apple Silicon)
and Linux**. The `.dll` name is just .NET's file extension on every OS. The bundled plugins are built this way.

What makes a plugin Windows-only:

- targeting `net8.0-windows` instead of `net8.0`;
- calling Windows APIs directly (P/Invoke into `user32.dll` and so on);
- shipping native libraries (`.dll`, `.dylib`, `.so`) for one OS only.

Plugins don't need any of that. Capture, OCR and the window live in MapleLite's platform layer, and plugins reach them
only through `IPluginContext`. On a Mac, users get MapleLite as a self-contained `.app` with the .NET runtime inside,
so they don't install .NET themselves. Plugins are loaded by that runtime, not by macOS, so they need no separate
signing.

## Support Development

If you find this app useful and would like to support its continued development:

- [Sponsor me on GitHub](https://github.com/sponsors/vahnx)
- [Support me through PayPal](https://paypal.me/twitchplaying)
- Bitcoin: `bc1qnl9nvwldzzdnlglqhvlhdm5l04rxawgkezr2ru`

Join the [vahnx Projects Discord](https://discord.gg/bn4HcQWTp) for general support, suggestions and project discussion.

Thank you for your support!

## Disclaimer

This is an unofficial fan project. It is not affiliated with, endorsed by or sponsored by Nexon. MapleStory and its
artwork are trademarks and property of Nexon. Use it in line with the game's terms of service.
