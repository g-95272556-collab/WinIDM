# WinIDM.App.E2E - real UI tests

UI automation tests for `WinIDM.App.exe` (WPF, .NET 9). The tests drive the real exe through
UI Automation (`System.Windows.Automation`, no extra NuGet packages), exercise real downloads
against a local server, and read the browser DOM through the Chrome DevTools Protocol.

## How to run

1. Publish the exe (including the smoke tests):

   ```
   powershell -ExecutionPolicy Bypass -File build\publish.ps1
   ```

2. Run only the E2E tests (category `E2E`):

   ```
   dotnet test tests\WinIDM.App.E2E\WinIDM.App.E2E.csproj --filter "Category=E2E"
   ```

The target exe is `artifacts\WinIDM\WinIDM.App.exe`. To test another build, set the
`WINIDM_EXE` variable (for example: `src\WinIDM.App\bin\Debug\net9.0-windows10.0.19041.0\WinIDM.App.exe`).

Requirements: Windows 10 19041 or later, the WebView2 Runtime, and an interactive desktop used
exclusively during the tests (see "What cannot be fully automated" below). No internet is required.

## How the tests work

- Each test launches the exe with `--data-dir` pointing at a unique temporary folder under `%TEMP%`
  (it does not touch `%LOCALAPPDATA%\WinIDM`). A pre-seeded `settings.json` is written before launch.
- The process is always terminated in `try`/`finally` (graceful close, then `Kill(entireProcessTree)`).
- The download, browser and media targets are a Kestrel server on `127.0.0.1` with a random port
  (`Support\FixtureServer.cs`). It supports Range (206), HEAD, per-connection rate limiting, and records every request.
- PNG screenshots are written to `tests\WinIDM.App.E2E\artifacts\`: `PASS_<scenario>.png` for every
  passing scenario, `FAIL_<scenario>.png` on failure. On failure, the app logs (`logs`) are also copied
  to `FAIL_<scenario>-logs\`. The S03 test also writes `S03-requests.txt` (the server request log).
- Tests run one at a time (`AssemblyInfo.cs`: `DisableTestParallelization`).

## What is covered

| Scenario | Test | What is verified |
|---|---|---|
| S01 | `MainWindowTests.S01_*` | Main window "WinIDM", exactly 11 filter tabs, eight toolbar buttons present. |
| S02 | `DownloadTests.S02_*` | A 2 MiB URL is added through the real Add URL dialog; the file completes with the correct SHA-256; the row moves to the "Completed" tab. |
| S03 | `DownloadTests.S03_*` | A 4 MiB download rate-limited to 256 KiB/s per connection is paused ("Pause all"); traffic really stops; it is resumed ("Start all"); the final hash matches; resume uses range requests from the checkpoint (finished segments are not re-downloaded). |
| S04 | `DownloadTests.S04_*` | Row context menu (Resume, Pause, Open, Open folder, Copy link, Checksum, Properties, Remove); Copy link really puts the URL in the clipboard; the Checksum dialog shows the correct SHA-256; Pause/Start all; search; "Completed" tab filter. |
| S04B | `DownloadTests.S04B_*` | Regression: the clipboard is locked by another app; a message is shown and the app stays alive. |
| S05 | `SettingsTests.S05_*` | The "Number of parts" setting is changed to 5; it is saved to `settings.json`; after the app restarts the value persists (and the Add URL dialog uses it). |
| S06 | `BrowserTests.S06_*` | The WebView2 browser loads a local page (`readyState` = complete and the title is read through CDP); the ad blocker blocks `/ads/*` while ordinary resources still load; the user script is injected and modifies the DOM; the Media panel detects `clip.mp4` and the Download button sends the download (hash matches); a password form over HTTP does not offer to save. |
| S07a | `ToolWindowsTests.S07_TorrentWindow_*` | The Torrent window opens without an error box. |
| S07b | `ToolWindowsTests.S07_BackupWindow_*` | Backup and restore opens; a local backup is created in a temporary folder outside the user data. |
| S07c | `ToolWindowsTests.S07_ThemeEditor_*` | The theme editor opens from the main window navigation rail ("Custom theme") and closes cleanly. |

## Bugs found and fixed

1. **Copy link threw an unhandled exception** (`src\WinIDM.App\ViewModels\MainViewModel.cs`, `CopyLink`).
   `Clipboard.SetDataObject` can throw `COMException` when the clipboard is locked. That exception
   reached the global handler and showed "An unexpected error occurred". Fix: `Services\ClipboardWriter`
   retries up to 5 times with an awaited 150 ms delay (the UI thread never sleeps), then shows the message
   "The clipboard is in use by another app. Try again.". The browser's Copy link, the Quality window and the
   torrent magnet copy use the same writer, so all of them carry the CPFROM1DM marker. Regression: `S04B`.
2. **The ad blocker rules and whitelist in `adblock.json` had no effect in the browser**
   (`src\WinIDM.App\Services\BrowserServices.cs`, `RebuildEngine`).
   `Composition.AdBlocker` loads `adblock.json`, but the browser used `BrowserServices.Engine`,
   which was built only from the filter lists and host whitelist. Fix: `RebuildEngine` now also
   calls `AdblockConfigStore.ApplyTo(engine)`. Regression: `S06` (`/ads/*` blocking).

Still a plain `Clipboard.SetText` (the global handler catches a locked clipboard): `Views\TextInfoWindow.xaml.cs`
and the user-script `GM_setClipboard` bridge. `PasswordManagerViewModel` handles the exception itself.

## What cannot be automated fully

- **Password saving over HTTPS** (positive path): not implemented. The browser offers to save only over
  HTTPS, and a local HTTPS certificate requires a CA trusted by WebView2. The test only checks the
  HTTP negative path (the "Save / Not now" bar does not appear).
- **Installing user scripts through the UI** (file/URL): not automated. The script is seeded into
  `userscripts.json` with the same format the app writes. Injection and its effect on the DOM are fully tested.
- **Tray icon menu**: still not automated. The icon tooltip is "WinIDM", but on Windows 11 this icon does not
  appear as a UIA element, even with the "Show Hidden Icons" overflow open. This no longer blocks S07c: the
  theme editor is also reachable from the main window navigation rail, which is what the test drives. The tray
  menu itself would need `Shell_NotifyIcon`/`TaskbarCreated` detection or Win32 messages for its menu.
- **Browser window**: its title is "<page title> - WinIDM Browser". The test looks it up by its suffix.
- **Focus and the clipboard are shared**: UI Automation requires an interactive desktop. Other applications
  can steal focus or lock the clipboard. The test makes sure clicks are sent only when the click point
  (UIA `FromPoint`) belongs to the WinIDM process, and retries when the clipboard is busy. If the WinIDM
  window is covered by another window, the click is canceled and the test fails instead of clicking another
  application. Do not use the computer while the suite is running.
- **Certificates and proxy**: none. The test server is HTTP only on `127.0.0.1`.
- **Restore**: not tested in this suite. Only local backup creation.
- **Windows Sandbox / headless CI**: not provided. The tests require an interactive user session.

## Structure

- `Support\AppSession.cs` - launch, graceful close, process-tree kill, temporary data folder.
- `Support\Uia.cs` - find, wait, click (with hit-test verification), type text, dump the UIA tree.
- `Support\AppFlows.cs` - shared UI flows (Add URL, tabs, context menu, dialogs).
- `Support\FixtureServer.cs` - Kestrel fixture server (Range, rate limiting, request log).
- `Support\Cdp.cs` - minimal DevTools client (`Runtime.evaluate`) for WebView2.
- `Support\Shots.cs` - PASS/FAIL screenshots and log copies.
- `Tests\*.cs` - scenarios S01 to S07.
