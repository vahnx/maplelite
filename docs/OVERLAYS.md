# Overlays: drawing on top of the game

**Status: built for Windows (windowed and borderless). Off by default: Settings → Overlay. Respawn Timer is the first plugin to draw on it.**

MapleLite shows everything in its own window first. This document covers how an overlay could
work with the least risk, given that MapleStory runs anti-cheat and can run windowed, borderless or in
exclusive fullscreen.

## What we know and don't know

- MapleStory clients ship anti-cheat. We have **not** confirmed which system Classic World uses or
  what it flags, and Nexon hasn't said whether overlays are allowed. Nothing below is a guarantee.
- What anti-cheat systems go after is well known in general: **code inside the game process**
  (DLL injection, hooking DirectX `Present`, which is how the Steam and Discord in-game overlays draw),
  **reading or writing game memory**, **packet tampering**, and **synthetic input**.
- A normal, separate desktop window that happens to sit above the game is a different category. It's what
  OBS previews, Discord's pop-out, music widgets and stream chat windows do. It still isn't *guaranteed*
  safe, because an anti-cheat can enumerate top-level windows and decide anything it likes.

## Hard rules for any overlay

1. **Never inside the game.** No injection, no graphics hooks, no `SetWindowsHookEx` with a DLL, no
   reading the game's memory. The overlay is MapleLite's own window, in MapleLite's own process.
2. **Display only.** The overlay window is fully click-through (`WS_EX_TRANSPARENT | WS_EX_LAYERED`,
   `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW` on Windows; `ignoresMouseEvents` on macOS; input-shape
   empty on X11). It never takes focus and never sends input. Plugins draw into it through an API with
   no input methods, the same as the rest of the plugin API.
3. **Off by default, opt-in per plugin.** A global switch in Settings, then a per-plugin switch.
   A global hotkey (registered by MapleLite, never sent to the game) hides it instantly.
4. **No gameplay automation cues.** The hub review rejects overlays that act as bots-by-proxy
   (e.g. "press now" timers for skills). Information overlays only: prices, chat highlights, EXP rate.
5. **Not in our own captures.** Window capture of the game reads only the game window's own picture, so
   MapleLite's OCR never sees the overlay. The overlay is *not* marked `WDA_EXCLUDEFROMCAPTURE`: with that
   set it didn't show on screen in live testing. The monitor-capture fallback only happens in exclusive
   fullscreen, where the overlay is off. Screen recorders (OBS display capture) will show it.

## Window modes

| Game mode | Can a separate window draw over it? | Plan |
|---|---|---|
| Windowed | Yes | Overlay follows the game's client rect. |
| Borderless fullscreen | Yes (the desktop compositor is still in charge) | Same as windowed. |
| Exclusive fullscreen | **No.** A topmost window either doesn't show or forces the game out of fullscreen/minimizes it. The only way to draw there is hooking the game's renderer, which rule 1 forbids. | Overlay auto-hides. The host shows: "Overlay paused: switch the game to borderless to see it." Everything stays in the MapleLite window (put it on a second monitor). |
| Minimized | n/a | Overlay hides. |

The platform layer already reports the mode (`GameWindow.Mode`). On Windows: a framed or
non-covering window is *Windowed*; a frameless window covering its monitor is *Borderless*; if it is
also in front while Windows reports a Direct3D fullscreen app running (`SHQueryUserNotificationState`
= `QUNS_RUNNING_D3D_FULL_SCREEN`) it is *ExclusiveFullscreen*.

## Following the game window

- Poll the game's client rect from the capture loop (already done ~1/s) plus `SetWinEventHook` with
  `WINEVENT_OUTOFCONTEXT` for `EVENT_OBJECT_LOCATIONCHANGE` / `EVENT_SYSTEM_FOREGROUND` on that window.
  Out-of-context hooks run in MapleLite's process. Nothing is loaded into the game.
- Hide when the game isn't the foreground window (so the overlay doesn't float over the browser),
  unless the user chooses "always show".
- DPI: the host is per-monitor-v2 aware, so rects are physical pixels on every monitor.

## Per OS

- **Windows**: layered, click-through, topmost tool window as above.
- **macOS**: borderless `NSWindow`/`NSPanel`, `level = .floating`, `ignoresMouseEvents = true`,
  `collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]` so it can sit over a
  fullscreen game Space. Follows the game via `CGWindowListCopyWindowInfo` bounds.
- **Linux**: the game runs under Wine/Proton as an X11/XWayland window. On X11, an override-redirect
  ARGB window with an empty input shape (XShape) works. On Wayland, apps can't place windows over
  other apps; the overlay is unsupported there (compositor-specific layer-shell isn't portable).

## Decision needed

1. Is it acceptable to build the overlay behind an "experimental" switch, before Nexon says anything?
2. Should it show only while the game is in front, or always?
3. Which plugins get overlay access first (suggestion: chat highlights from Chat History, EXP rate from
   XP Tracker)?
