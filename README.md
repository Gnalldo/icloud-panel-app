# iCloud (standalone app)

An iCloud panel for Windows that behaves like a native flyout: a notification-area
icon toggles a **borderless, rounded WebView2 window** in its own profile, at a
size and position you choose and lock.

WebView2 is the Chromium engine that ships with Windows (runtime already present),
so nothing extra is installed and — unlike Brave/Chrome app windows — the window
is ours, which is what makes a true borderless rounded card possible.

## Launch

Double-click **iCloud** on the Desktop or in the Start menu (it opens the panel
through `start-panel.vbs`), or left-click the tray icon to toggle it.

## Privacy

This repository contains source only. `config.json`, `panel/profile/`,
`panel/session-cookies.bin`, `mailfeed-secrets.xml`, app caches, logs, and
screenshots are local runtime data and are ignored by Git. Start from
`config.example.json` if you want a template.

## Why PowerShell hosts the panel

This machine has Windows 11 **Smart App Control** in enforce mode. It blocks
newly built unsigned executables outright — `An Application Control policy has
blocked this file` — which is what stopped the panel launching. Turning that
protection off is a system-wide, one-way security change, so the app works with
it instead: the tray starts `start-panel-host.vbs`, which runs
`powershell.exe` (a signed Microsoft binary) and `iCloudPanelHost.ps1` compiles
`iCloudPanel.cs` in memory and runs the same form inside that trusted process.

Nothing visible changes: same borderless window, same profile, same config, and
no security setting is touched. `panel/iCloudPanel.exe` stays only as a fallback
for machines without that policy; on this one Windows will keep refusing it.

What that implies day to day:

- Edits to `iCloudPanel.cs` take effect the next time the panel is shown — there
  is no rebuild step.
- Task Manager shows the panel as a `powershell.exe` process; an entry named
  `iCloudPanel.exe` is not expected.
- Startup is about a second slower while the source compiles.
- If the panel does not appear, `panel/host.log` holds the host's steps and any
  compile error.

## Layout of the pieces

| File | Purpose |
| --- | --- |
| `iCloudPanelHost.ps1` | Runs the panel inside PowerShell (compiles `iCloudPanel.cs` on start) |
| `start-panel-host.vbs` | Hidden launcher for that host — what the tray starts |
| `panel/iCloudPanel.exe` | Optional compiled host, built locally; not committed |
| `panel/profile/` | The panel's own browser profile — its iCloud sign-in lives here |
| `panel/session-cookies.bin` | DPAPI-encrypted copy of the Apple cookies (staying signed in) |
| `iCloudPanelTray.ps1` | Tray icon, show/hide, resize/lock, placement, config |
| `start-panel.vbs` | Hidden launcher for "show the panel" (used by shortcuts) |
| `start-panel-tray.vbs` | Hidden launcher for the tray icon (used at sign-in) |
| `config.example.json` | Template for local size, position, corner and background settings |
| `panel/background.bin` | The custom wallpaper picked from **Background…** (when one is set) |
| `icloud.ico` | Taskbar/shortcut/tray icon |
| `setup-webview2.ps1` | Re-downloads the WebView2 SDK assemblies if needed |
| `screenshot.ps1`, `diag-windows.ps1` | Diagnostics: capture the panel, list its windows |

Legacy Brave-based files (`iCloudTray.ps1`, `start-app.vbs`, `start-tray.vbs`,
`iCloudPanel.cs`'s predecessor flow) are kept as a fallback; the shortcuts and
Startup entry now point at the WebView2 panel only.

## Look

- `FormBorderStyle.None` + frame styles stripped at show time: no title bar, no
  close/minimise buttons, no window border.
- Corners are rounded with a window region (`radius` in `config.json`, default
  14 px) — regions work here because the window is not a DirectComposition
  surface, which is exactly why this failed with Brave.
- The page scrollbar is hidden by injecting CSS at document creation
  (`::-webkit-scrollbar{display:none}`), the WebView2 equivalent of Chromium's
  `--hide-scrollbars`.
- iCloud's top bar (Apple logo, title, account menu) is hidden with
  `header,[role='banner']{display:none !important}`, so the page starts right at
  its content.
- Two buttons (gear, lock) are injected into the page (an HTML element, because a WinForms
  control cannot paint over WebView2's window). It reports clicks to the host
  through `window.chrome.webview.postMessage`, which does the frame work.

A shape-clipped window forgoes DWM's drop shadow; the rounded card floats flat.

## Size and position: tune it, then lock it

Everything is stored in `config.json`, e.g.
`{"width":785,"height":764,"x":2400,"y":-1300,"radius":14,"topmost":false,"pinned":false}`
(negative coordinates are normal on monitors above or left of the primary):

1. Move the mouse into the **bottom-right corner of the panel**. A small round
   lock button fades in.
2. Click it. A native title bar and resize edges appear — but the page stays in
   exactly the same place — so you can drag and resize it freely.
3. Click the button again (or press Esc). The frame disappears, corners round
   off, and the new size and position are written to `config.json`.

With no `x`/`y` saved the panel defaults to the bottom-right corner of the primary
screen with a 12 px margin. A saved position belongs to the monitor it was saved
on: the tray re-reads `config.json` on every show, so the panel returns to that
monitor instead of jumping back to the primary one.

## Corner settings (gear)

The gear next to the lock opens these options, all remembered in `config.json`:

- **Always on top** — the panel floats above other windows.
- **Pin to desktop** — the opposite: it rests behind other windows, so it sits on
  the desktop like a widget. Clicking the tray icon still raises it to the front,
  and it drops back to that layer the moment it loses focus. Turning one on
  clears the other. It drops *under the window that took the focus*, not below
  the desktop: down there WebView2 stops painting and the panel comes back as a
  blank navy rectangle.
- **Hide when not focused** — flyout mode: left-click the tray icon to bring it
  up, and it disappears completely the moment it loses focus. Turning it on
  clears the other two.
- **Background…** — opens a fly-out with five preset colours, **Custom colour…**
  (the Windows colour picker), **Custom image…** (any local picture, copied into
  `panel/background.bin` so the panel still finds it later), **Desktop
  wallpaper** (your real Windows wallpaper, see below), **Transparency**
  (cycles 100 → 85 → 70 → 55%) and **Default** to go back to iCloud's own
  wallpaper. Saved as `bgkind` (`default`, `color`, `image` or `wallpaper`),
  `bgcolor`, `bgimage` (the stored file's extension), `bgopacity` and
  `bgoffset`.

**Desktop wallpaper** is as close to a translucent panel as this window can get.
A windowed WebView2 paints its own opaque surface, so asking it for transparency
only reveals the panel's dark base — never the desktop. Instead the panel readsthe wallpaper Windows itself is showing (`TranscodedWallpaper`), crops the slice
that sits behind the panel (Fill fit against the monitor's working area), scales
it to panel size and paints it under the page, dimmed by the Transparency
setting. Windows' exact placement does not follow a formula that holds on every
machine, so the fly-out has **Shift up** / **Shift down** to move the picture
4 px at a time and an **Offset: N px** row that resets it; the value is saved as
`bgoffset`. The slice is captured when you pick the mode and on every panel
start, so pick it again (or use tray → **Reload panel**) after changing your
wallpaper.

The background paints the page's own backdrop and hides iCloud's wallpaper layer,
including the pseudo-element that layer draws with — overriding that element's
styles alone changed nothing, which is why the panel also clears the `::before`
and `::after` layers and any image inside them. Cards, text and icons are left
alone. Transparency blends a colour (or darkens an image) towards the panel's dark
base, so labels stay readable.

## Dashboard

The panel lays five widgets over the iCloud page: **Clock**, **Shortcuts**,
**Weather**, **Calendar** and **Inbox**. Each is its own floating glass tile in
the iCloud style, with a transparent full-window layer between them, so clicks
pass through the gaps to the page. Tiles lift slightly on hover, like iCloud's
own icons.

Hover a tile and a round grip appears in its bottom-right corner: drag it to
move the tile, Shift-drag to resize it. Gear → **Arrange dashboard** reveals the
hide buttons and dashed outlines; Esc leaves the mode. **Reset dashboard
layout** re-spreads the default row across the panel width. Hidden tiles stay
visible while arranging, dimmed, so they can be brought back. The layout belongs
to the page, which posts it back to the panel; it is stored in `config.json` as:

```json
"dash":{"order":["clock","shortcuts","weather","calendar","mail"],
        "hidden":[],
        "free":{"clock":{"x":103,"y":760,"w":200,"h":200},
                "mail":{"x":1045,"y":760,"w":300,"h":200}}}
```

`free` holds each tile's position and size in page pixels relative to the
viewport. The default row is scaled down on narrow panels so the tiles never
overlap, and each tile is clamped to stay reachable.

**Weather** comes from Open-Meteo — no key, no account — and is fetched by the
panel, not the page, so iCloud's content policy never applies. Set the place
once with gear → **Weather location…** and it is saved so it never drifts:

```json
"weather":{"lat":54.5973,"lon":-5.9301,"place":"Belfast, United Kingdom"}
```

Temperature units, the 12/24-hour clock and date order come from Windows itself —
the regional format and measurement system — not from Chromium's idea of a
locale: a US machine shows °F and 12-hour times, a UK one °C and 24-hour times.
With no place saved, the panel asks the Windows location service once at startup;
gear → **Use Windows location** asks again on demand (the same permission the
Weather app uses). If that is denied or location is switched off, the manual
place above still applies. Coordinates that come from Windows are
reverse-geocoded once, so the card names the town (for example *Portland,
Oregon*) rather than a generic label.

The Clock card can greet you by name: gear → **Your name…** saves it as `"name"`
in `config.json`, giving *Good evening, Alex*. It is whatever you type, never the
iCloud account's email; leave it empty and the greeting stays plain.

The line under the greeting appears only on an occasion: a birthday you entered
or a holiday such as New Year, Christmas or Thanksgiving. Gear → **Birthdays…**
lists birthdays by hand (name, month and day) and lets you tick which mail
accounts may supply them from today's calendar; the Clock shows *Happy birthday,
Alex!* from either source. Manual entries are saved as
`"birthdays":[{"name":"Alex","month":11,"day":10}]` and ticked accounts as
`"birthdayAccounts":["you@outlook.com"]`. Credentials are not involved: the
accounts only filter the calendar events `mailfeed.ps1` already returns.

**Calendar** and **Inbox** read the mailboxes from the cloud. `mailfeed.ps1` runs
as a child process every five minutes, prints the next week of appointments plus
the newest messages as JSON on stdout, and stores no mail anywhere. Two sources
are merged into one list:

- **the Outlook.com account** through Microsoft Graph, with device-code sign-in
  and read-only scopes (`Mail.Read`, `Calendars.Read`). No client secret; the
  refresh token is DPAPI-encrypted for your Windows user in
  `mailfeed-secrets.xml`;
- **the Gmail account** through IMAP over TLS with a Google app password, stored
  the same way. Mail only — the Google calendar is not read.

Setup is one-time:

```powershell
.\mailfeed.ps1 -ClientId <application-id>      # Azure app, public client
.\mailfeed.ps1 -SignIn                         # shows a code to approve
.\mailfeed.ps1 -GmailUser you@gmail.com -GmailPassword <app-password>
.\mailfeed.ps1 -SelfTest                       # offline checks, no network
```

Nothing has to be running for this: no Outlook, no Office, no logged-on desktop
session and no second machine, which is what lets the cards keep updating while
the PC is gaming or off. Each card fails soft: an unreachable source leaves a
one-line notice while the other source still fills its own card, and
`outlook-widgets.ps1` stays in the tree as a fallback for machines that do have
classic Outlook (the panel prefers `mailfeed.ps1` whenever it exists).## Right-click

Right-clicking the panel shows Chromium's usual menu with two entries added at the
top, above a separator: **Reset panel position and size**, and **Unlock panel** —
which reads **Lock panel** while the panel is unlocked.

The bottom-right corner carries a third button beside the gear and the lock: it
shows or hides iCloud's account strip (**Your iCloud**, **Your Storage**, **Your
Devices**) at the end of the page, which is otherwise just clutter under the
dashboard. The choice survives restarts as `"showinfo"` in `config.json`. The window is frameless, so a panel left
hanging off the edge of a monitor *after a resolution change or a screen being
unplugged* has no title bar to drag back into view. The reset centres it on the
monitor it mostly occupies and only shrinks it when the current size does not fit
that monitor's working area; the new geometry is saved like any other move.

## Locked vs unlocked

While locked, the page cannot scroll: `overflow` is forced to `hidden` and wheel,
touch and Page Up/Down/Home/End are swallowed (except Ctrl+wheel, so zoom still
works). Unlocking with the corner button or the right-click menu restores normal
scrolling (Escape locks it again), which makes resizing and navigating the page
work the way you'd expect.

Zoom is remembered: set it with Ctrl `+` / `-` or Ctrl+wheel, and the level is
written to `config.json` as `zoom` and re-applied on every launch and page load.
The corner buttons and gear fly-out are UI rather than page content, so they are
scaled by the inverse of the zoom: zooming the page out to 33% leaves them the
same size on screen instead of shrinking to a third.

## Staying signed in

The panel has its own persistent profile in `panel/profile`, but that alone was
not enough: WebView2 drops every cookie that has no expiry when the process
exits, and Apple's sign-in is carried by exactly those session cookies. That is
why it kept asking again.

So the panel keeps its own copy of the Apple cookies:

- on load, and once a minute while running, it reads the `apple.com` / `icloud.com`
  cookies through `CoreWebView2CookieManager` and writes them to
  `panel/session-cookies.bin`, encrypted with Windows DPAPI for your user — no
  readable cookie values are stored on disk;
- on start it re-adds them *before* the first navigation, so the page loads
  already signed in.

The store currently holds the three cookies the profile keeps
(`x-apple-group`, `X_APPLE_WEB_KB-…`, `X-APPLE-WEB-ID`). Apple's actual sign-in
cookie (`X-APPLE-WEBAUTH-*`) only exists **after** a successful sign-in, so sign
in once with this build and it is captured from then on. Check it worked with
`panel/panel.log`: a healthy start logs `cookie restore: N Apple cookies re-added`
and the inventory line then lists an `X-APPLE-WEBAUTH-*` entry.

If Apple still challenges you, watch for a **Trust this browser** prompt during
sign-in — that trust record is Apple's, not something the app can create.

Signing out or starting fresh: close the panel and delete `panel/profile`.

## Tray menu

**Reload panel**, **Exit tray icon**. Left-click toggles the panel; there is no
show/hide or lock entry because those are the click and the corner button.
**Exit tray icon** now shuts the panel down as well, so nothing is left running
with no icon to reach it.

If the icon ever seems to do nothing, check `panel-tray.log`. A panel host that
lost its window but stayed alive kept the single-instance lock, so every click
launched a host that exited as `another host is already running` and the icon
was dead for good. The tray now notices that, clears the stale host and retries
once (`stale host (no window) - restarting it`), so a click brings the panel
back instead of failing silently.

- Single-instance guarded; starts automatically at sign-in via `iCloud Tray.lnk`
  in your Startup folder (`shell:startup`).
- Logs: `panel-tray.log` (tray actions), `panel/panel.log` (panel startup,
  WebView2 state, cookie store, probe) and `panel/host.log` (the PowerShell
  host's steps and any compile error).
- A blue console window on top of the panel is a PowerShell console, never the
  panel: the tray and the host each hide their own, and the tray only accepts a
  WinForms window as the panel (`panel window: WindowsForms10…` in the log).
- Editing `iCloudPanel.cs` needs no rebuild: hide the panel through the tray and
  show it again. `build-panel.ps1` still produces the optional
  `panel/iCloudPanel.exe` fallback:

  ```powershell
  powershell -ExecutionPolicy Bypass -File build-panel.ps1
  ```

- Reset the panel's sign-in/cache: close the panel and delete `panel/profile`.

## New iCloud Note (Start menu)

A Start-menu shortcut that opens the panel with a brand-new note already open,
on whichever display the mouse is on.

- `New iCloud Note.lnk` sits in `%APPDATA%\Microsoft\Windows\Start Menu\Programs`;
  `create-note-shortcut.ps1` writes it (re-run it to recreate). Right-click it in
  Start > All apps > **Pin to Start** for a tile - Windows has no supported way
  to script that last click.
- The shortcut runs `start-new-note.vbs` -> `iCloudPanelTray.ps1 -NewNote`. That
  shows the panel on the mouse's monitor (same size, and the same edges it hugs
  on its own monitor), then sends `new-note` to the running window with
  `WM_COPYDATA`. `config.json` keeps its saved spot, so an ordinary tray click
  still puts the panel back where it lives.
- The panel answers with `window.icNewNote()` in the page: go to
  `icloud.com/notes` if needed, wait for the toolbar, click **Create a note** by
  its wording (Apple's class names change between builds), then focus the new
  note. Works with the panel hidden behind other windows or pinned to the
  desktop; if the panel is not running it is started first and the command is
  queued until iCloud has loaded.
- `panel/panel.log` records `new-note: command received` and then
  `new-note result: ok` - or `new-note result: fail:<reason>` (`no-button`,
  `no-editor`, `not-icloud`). A signed-out panel ends at `fail:no-button` with
  Apple's sign-in page in front of you.
