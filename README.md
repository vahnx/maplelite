# MapleLite

A plugin host for **MapleStory Classic World**, in the spirit of RuneLite. It runs on **Windows, macOS
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
| `plugins/*` | Bundled plugins: MapleMemory, Sale Card, XP Tracker. |
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

Needs the .NET 8 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/MapleLite.Host -f net8.0-windows10.0.19041.0
```

macOS / Linux: `dotnet run --project src/MapleLite.Host -f net8.0`, or publish with `-r osx-arm64` / `-r linux-x64`.

Test switches (run the built `MapleLite` executable). None of them touch the live game:

- `--replay <png>`: run the whole host on a saved capture instead of the game
- `--ocrtest <png> --out <txt>`: read a saved capture with the shared OCR and write the lines
- `--snapshot <png> [--theme <id>] [--page game|settings|<plugin-id>]`: render the window to a PNG and exit

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
