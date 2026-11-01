# IDM+ feature parity

How the functions of IDM+ (1DM+ v20.0, Android) map to WinIDM. Sources: `research/01`, `02`, `03` and the 325-key
settings table `03a`. Statuses:

- **Added** — implemented in the parity round of 1 Nov 2026 (with tests where it is logic).
- **Fixed** — existed but did not work as IDM+ does; corrected in the same round.
- **Present** — already in WinIDM before that round.
- **Android only** — tied to phone hardware or Android UI; no Windows meaning.
- **Not copied** — left out on purpose (privacy, security or policy).
- **Gap** — a real IDM+ function that WinIDM still does not have.

## Download engine

| IDM+ function | Status | Notes |
|---|---|---|
| Parts = min(n, size / 2 MiB), 32 max, per-domain part limit, per-site download limit | Present | `SegmentPlanner`, `SiteRules` |
| Advanced download method: twice the parts for files of 32 MiB+, extra parts **queued** | Fixed | Extra parts used to run as extra connections; now at most n run, the rest start as parts finish (`SegmentPlanner.WorkerCount`). Switch in Settings > Advanced |
| Auto restart non-resumable links | Added | Off: an interrupted non-resumable download stops with error 186. On: restarts from zero |
| Pause warning for servers without resume (`resume_not_supported_server`) | Added | Confirmation before pausing |
| Battery limit | Added | `GetSystemPowerStatus`; a PC without a battery is never blocked |
| Remove old links after N days | Fixed | 0 used to remove every finished record; 0 and -1 now mean off |
| Parts / simultaneous downloads settings | Fixed | The page showed the count but stored it raw while the engine adds 1 (IDM+ stores count - 1); defaults ran 4 downloads x 9 parts |
| Convert .ts to MP4 with FFmpeg | Present | Status label corrected |
| Retries, If-Range resume, speed limits, Wi-Fi only, metered limits, scheduler, move after finish, checksum | Present | If-Range is an improvement over IDM+ |

## Download list and tools

| IDM+ function | Status | Notes |
|---|---|---|
| Progress for each segment (`idm_pref_show_multi_part`) | Added | Per-part bar under the progress bar |
| Download count with tab name (`sdcwtn`), file-type filter switch, time filter | Added | |
| Batch download (`[1-10]`, `[01-10]`, `[a-z]`, `[0-100:5]`) | Added | `BatchPattern`, 10,000 link cap |
| Import / export links | Added | Text, CSV, JSON, bookmarks HTML in; text out |
| Remove leftover part files | Added | HLS/DASH `.video.*` / `.audio.*` tracks no unfinished download needs |
| Open web pages in the browser automatically | Added | HEAD check when the switch is on |
| Disable animations | Android only | WinIDM has no animations to turn off |

## Notifications

| IDM+ function | Status | Notes |
|---|---|---|
| Sounds: start, error, complete, all complete | Added | Empty, `default` (Windows sound) or a `.wav` path |
| Download added toast and its switch | Added | Batch and import report once |
| Progress during downloads | Added | Tray tooltip and menu line |
| Overlay window above all apps (size, alignment, offsets, colors, text size, combined line) | Added | No-focus, always-on-top window; sizes are percent of the screen work area |
| Combine/clear notifications, progress bar or action buttons in notifications, lock screen, vibration | Android only | Notification shade features |

## Browser

| IDM+ function | Status | Notes |
|---|---|---|
| Find in page, view source, print, translate, zoom / text size, full screen | Added | Ctrl+F, F3, Ctrl+U, Ctrl+P, Ctrl+/-/0, F11; translate opens Google Translate on request |
| Reopen closed tab, tab closed message with Undo | Added | Ctrl+Shift+T |
| Show target URL of a link (`stupm`, `tumnoc`) | Added | In the status bar (a WPF overlay cannot draw over WebView2) |
| Rendering mode, force text selection/copy/paste | Added | Injected CSS |
| Safe browsing | Added | Microsoft Defender SmartScreen |
| Enable cookies, cookies in incognito | Added | |
| Camera / microphone / location default answers (normal and incognito) | Added | |
| Ask before opening new tabs | Added | |
| Use page title as media file name | Added | |
| Automatic subtitle capture, subtitles in the videos folder | Added | `.vtt/.srt/.ass/.ssa/.ttml` by MIME or extension |
| Saved web pages folder, page title as file name | Added | |
| Delete all bookmarks | Added | Asks for confirmation |
| Bookmark folders, show folders first | Added | "Move to folder…" in the bookmark list; old bookmark files load unchanged |
| Bookmarks/bookmarklets in search suggestions | Gap | The address bar has no suggestion list |
| Tabs, incognito, popups, ad blocker, element hiding, grabber, reader + TTS, saved pages, user scripts, passwords, DNS, permissions, WebRTC leak, SSL, desktop mode | Present | |
| Text encoding, DRM handling | Gap | WebView2 has no API for a default encoding; DRM is out of scope |
| Bottom address bar, swipe gestures, hand mode, floating buttons, pull to refresh, landscape, PiP aspect, refresh-rate match, overlay player gestures | Android only | |

## Proxy and torrent

| IDM+ function | Status | Notes |
|---|---|---|
| Proxy type HTTP / SOCKS5 / HTTPS | Fixed | SOCKS5 chosen in Settings was used as HTTP by the engine |
| Proxy provider: Manual, Orbot (Tor), I2P | Added | Presets 127.0.0.1:9050 SOCKS5 and 127.0.0.1:4444 HTTP |
| Disable WebRTC when using a proxy | Added | |
| Default trackers, tracker list URL, add trackers to duplicate torrents, always contact all trackers | Added | |
| Display peer info | Added | "Peers..." in the torrent window: address, client, progress, speeds, seeder, encryption |
| Merge disk writes: buffer size | Added | MonoTorrent disk cache |
| Folder view by default for file selection | Gap | The file list is flat |
| Separate disk thread, verify pieces from memory | Android only | MonoTorrent always does disk work asynchronously and has no piece-verify size setting; uTP and DHT limits are in `HANDOFF.md` |

## Not copied on purpose

Remote `set_settings` push (FCM), push notifications, `android_id`/e-mail collection, the theme marketplace, and the
IDM+ refusal of YouTube/Facebook/Instagram downloads (see `MEDIA-CAPTURE.md`).

## Special sources

| IDM+ function | Status | Notes |
|---|---|---|
| Google Drive file links | Added | Share links (`/file/d/ID/view`, `open?id=`, `uc?id=`) become the direct download, including large files; public files only |
| Dropbox and OneDrive share links | Added | `dl=0` → `dl=1`, `redir` → `download` |
| Google Drive folders (OAuth), MEGA files and folders | Gap | Need an account sign-in (Drive) or the MEGA API and its AES decryption |
| 1DM export text format | Gap | Its line specification was not recovered from the APK |
