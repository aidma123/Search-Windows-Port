# Search for Windows

A Windows port of [Search](https://searchbrowser.app), the small, fast, quiet browser for the Mac by
[Office Commun](https://officecommun.com) ([source](https://github.com/driceroland/Search), MIT).
This is an unofficial port: Office Commun does not make or support it.

A row of tabs, down the left or across the top, and the page. No toolbar, no start page,
no account, no telemetry.

## The engine

The Mac app uses WebKit because it ships with every Mac. The Windows equivalent is
**Microsoft Edge WebView2**: the Chromium-based engine already installed on every Windows 10
and 11 PC. Search doesn't bundle a browser engine of its own, so the app is a **~1.5 MB exe**.

## Download

Get the builds from the **Actions** tab (artifact `Search-windows`), or from Releases once a `v*` tag is pushed:

| File | Size | Needs |
|---|---|---|
| `Search-win-x64.exe` / `Search-win-arm64.exe` | ~1.5 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows offers to install it the first time) |
| `Search-win-x64-standalone.exe` / `…arm64-standalone.exe` | ~60 MB | nothing |

Both need WebView2, which Windows 10 and 11 already have. If it's missing, Search links to the installer.

## Memory

Search is built to use as little RAM as a Chromium-based browser can:

- **Tabs from your last session come back as addresses only.** Each one costs nothing until you click it.
- **A tab you leave is told to use as little memory as it can** right away (`MemoryUsageTargetLevel.Low`).
- **After 20 seconds in the background it is frozen** (`TrySuspendAsync`): no scripts, no timers, and memory is handed back to Windows where the engine can.
- **After 10 minutes it sleeps.** The page is closed and the tab is only an address and a title until you click it.
- Tabs playing sound are never frozen or put to sleep.
- **Minimising the window freezes every page** and compacts the app's own memory.
- **Same-site tabs share one page process** (`--process-per-site`).
- **Lean engine mode is on by default.** Chromium's low-memory-device mode (`--enable-low-end-device-mode`) shrinks the engine's caches. Some gradients may band slightly; turn it off in Settings › Memory.
- The app's own process uses workstation garbage collection with no background collection thread, and hands memory back rather than holding it.

All of these timings can be changed in Settings › Memory.

## What carries over

| Search for Mac | Windows | Shortcut |
|---|---|---|
| One field for addresses and searches, finished from your own history | ✓ | Ctrl+L, Ctrl+T, F6, Alt+D |
| Tabs down the side or across the top | ✓ | Ctrl+Shift+S |
| Fold the sidebar away; the left edge brings it back | ✓ | Ctrl+S |
| Pins: a letter or favicon, kept across restarts; closing one puts it to sleep | ✓ | Ctrl+D pins / unpins |
| Spaces, each with its own icon; "signed in" or "fresh" (own cookies and sign-ins) | ✓ (fresh spaces use their own WebView2 profile) | Ctrl+Alt+← / →, two-finger swipe over the tabs |
| Search open tabs | ✓ | Ctrl+K |
| Reading mode (the same extraction script as the Mac app) | ✓ | Ctrl+Shift+R |
| Floating video above every app | ✓ (Chromium picture-in-picture) | Ctrl+Shift+P |
| Ad blocker that runs before the page | ✓ (network-level request blocking plus the same short cosmetic list) | per site from the lock icon |
| Hide anything, for good, on that site | ✓ | Ctrl+Shift+H; review with Ctrl+Shift+U |
| Private tabs | ✓ (InPrivate) | Ctrl+Shift+N |
| Reopen closed tab | ✓ | Ctrl+Shift+T |
| Copy clean link (tracking parameters removed) | ✓ | Ctrl+Alt+C |
| History panel | ✓ | Ctrl+H |
| Web Inspector and console | ✓ (Edge DevTools) | F12, Ctrl+Shift+I, Ctrl+Shift+J |
| Chrome extensions from the Chrome Web Store | ✓ (WebView2 extension support; open the store page, then the puzzle button › Add) | |
| Passwords | ✓ saved by the engine, encrypted with your Windows account (not Windows Hello-gated) | |
| Light, dark, or as the system is | ✓ | |
| Default browser | ✓ Settings › This PC | |
| Rename, duplicate, mute, drag to reorder, middle-click to close | ✓ | |
| Find, print, zoom | the engine's own | Ctrl+F, Ctrl+P, Ctrl+ + / − / 0 |

**Not ported yet:** Split View, Import from other browsers, Sync, bookmarks bar, automatic updates,
the AI features, passkeys via Windows Hello, Translate, site styles, and the many smaller
features in the Mac app's 117k lines. Extension popups open in a small window rather than
anchored to a toolbar button.

## Where things are kept

Everything is in `%LOCALAPPDATA%\Search`: `settings.json`, `session.json`, `history.json`,
`hidden.json`, `extensions.json`, and the engine's data in `Web\`. Nothing leaves the PC except
the pages you ask for and their icons.

## Building

```
dotnet test tests/Search.Core.Tests
dotnet publish src/Search -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

The window is built in C# without XAML and references WPF directly, so it builds from Windows,
macOS or Linux (`EnableWindowsTargeting`). It only runs on Windows.

- `src/Search.Core`: platform-free logic (address parsing, search engines, the ad blocker,
  clean links, history, hidden elements, Chrome Web Store packages) plus the injected scripts. Unit tested.
- `src/Search`: the WPF window, WebView2 wiring, panels, settings.
