# WinIDM.Engine

Multi-segment HTTP(S) and HLS download engine for WinIDM. Built clean-room from the specifications
`research/01-enjin-muat-turun.md`, `research/03-ciri-ui-tetapan-pustaka.md` and `research/03a-jadual-tetapan.md`.
No telemetry. All tests use 127.0.0.1 only; no internet.

## Events

| Event | Arguments | When |
|---|---|---|
| `ProgressChanged` | `DownloadProgress` | Every 500 ms for active downloads |
| `ItemChanged` | `DownloadItem` | On every state or record change |
| `DownloadCompleted` | `DownloadCompletedInfo` | A download completed (after moving the file and removing the link if configured). Contains `Id`, `FileName`, `FullPath`, `Category`, `TotalBytes`, `Duration`, `MovedTo`, `ChecksumVerified`, `Notes` |
| `DownloadFailed` | `DownloadFailedInfo` | A download switched to `Error`. Contains `Id`, `FileName`, `ErrorCode`, `ErrorMessage` |
| `AfterDownloadsFinished` | `SystemActionRequest` | No active or waiting downloads after a download completed or failed. Once per batch. Contains `Action` (from `winidm_after_downloads_action`), `CompletedCount`, `FailedCount` |

The engine does **not** perform system actions (shut down, sleep, etc.). The App must obtain user confirmation first.

## Error codes

| Code | Meaning |
|---|---|
| 1 | Unknown error |
| 108 | Invalid length from the server, or `Content-Range` does not match |
| 118 | I/O error (file write, missing file) |
| 130 | Connection error (retryable), including a rejected proxy TLS certificate |
| 180 | SHA-256/MD5 checksum does not match |
| 181 | No longer emitted (formerly: SOCKS not supported) |
| 182 | Invalid proxy configuration (empty host, host contains a scheme, port outside 1-65535, unknown type) |
| 183 | Too many redirects (limit 8) |
| 184 | Unsupported playlist or segment (EXT-X-MAP with SAMPLE-AES, SAMPLE-AES segment that is not MPEG-TS, missing PAT/PMT, encryption other than AES-128 and SAMPLE-AES) |
| 185 | Invalid URL |
| 416, 429, 509 | HTTP: range not satisfiable, too many requests, bandwidth limit. Not retried at the segment level |
| Other | The actual HTTP status code (for example 404, 503) |

## Settings and defaults

- `idm_pref_download_count` (raw 2) and `idm_pref_download_part` (raw 7) are the 03a values. The engine adds 1, so **the effective defaults are 3 concurrent downloads and 8 parts**. The `SettingDefaults.EffectiveDownloadCount` and `EffectivePartCount` constants document this.
- **Speed limit units**: `idm_pref_speed_limit` is in **KiB/s**, active only when `idm_pref_speed_limiter` = true. The per-download limit via `SetSpeedLimitAsync(id, bytesPerSecond)` is in **bytes per second**.
- Proxy: `idm_pref_proxy_type` 0 = HTTP (HTTPS targets through CONNECT), 1 = SOCKS5 (with RFC 1929 auth), 2 = HTTPS (TLS to the proxy, see the Proxy section). Auth via `idm_pref_proxy_user` and `idm_pref_proxy_pass`.
- Secret keys (`IsSecret`: `*_pass`, `*_password`, `*_token`, `*_secret`) are encrypted with DPAPI (user scope) on disk only.
- Extra WinIDM keys: `winidm_default_save_directory`, `winidm_move_files_by_category`, `winidm_move_rules`, `winidm_after_downloads_action`, `winidm_hls_record_live_until_stopped`, plus the network policy keys below.

## Network policy (`INetworkPolicy`)

The `INetworkPolicy` interface: `IsOnline`, `IsWifiOrEthernet`, `IsMetered` and the `Changed` event. The App supplies
`NetworkPolicyAdapter` (`Windows.Networking.Connectivity`). Without a policy (`null`), the engine uses `DefaultNetworkPolicy`:
no restrictions, no metered detection, and Wi-Fi/Ethernet read through `NetworkInterface`.

The Windows interpretation of the settings:

| Setting | Key | Effect |
|---|---|---|
| Wi-Fi only | `idm_pref_wifi` | Download only if `IsWifiOrEthernet`. Ethernet counts as equivalent to Wi-Fi (not mobile data). 03a: "Ignore mobile internet" |
| Pause on metered data | `winidm_pause_on_metered` (default false) | If `IsMetered`, running downloads are stopped and go back to the queue |
| Mobile data limit | `winidm_metered_daily_limit_mb`, `winidm_metered_monthly_limit_mb` (MiB, 0 = none) | Bytes transferred while `IsMetered` are counted. Limit reached: downloads are stopped and not restarted until a new day/month |

- Downloads stopped by policy go back to **Queued** and restart automatically once allowed (unlike Paused, which the user starts). A user pause stays Paused.
- Re-evaluation happens on the `Changed` event and every 1 second (tick). Downloads resume from the checkpoint (ranges), not from zero.
- This is the only pause-on-metered policy. The App Settings row "Pause downloads on a metered network" writes this same key and starts no separate pause loop.
- Quota counts are stored in settings (`winidm_metered_usage_day`, `_day_bytes`, `_month`, `_month_bytes`) so they persist across restarts. Written every tick (1 second) and when the engine closes. Days and months are measured in local time.
- Engine `Dispose` disposes the policy if it is `IDisposable`.

## Host resolver (DoH/DoT)

`DownloadEngine.HostResolver` (or the ctor argument) is a `Func<string, CancellationToken, Task<IPAddress[]>>`. The engine uses it through `SocketsHttpHandler.ConnectCallback` for targets and proxy hosts. `null` means system DNS. Not used if a shared transfer handler is supplied to the engine.

## Proxy

- **HTTP** (0): HTTP targets through absolute-form; HTTPS targets through CONNECT.
- **SOCKS5** (1): with username/password auth.
- **HTTPS** (2): TLS to the proxy itself (`https://` scheme on `WebProxy`). .NET 9 supports this natively. The proxy certificate is validated against the system certificate store; an untrusted certificate causes error 130. A host containing `://` is rejected with 182.

## Per-site settings (`SqliteDownloadRepository`)

`SetSiteSettingsAsync`, `GetSiteSettingsAsync(host)` (longest domain match), `ListSiteSettingsAsync`, `DeleteSiteSettingsAsync`. Fields: UA, cookies (DPAPI encrypted), default folder, `UseForSubdomain`.

## Post-download

- Move rules: the first match wins; `extension` takes precedence over `category`. No overwrite: a unique name is used.
- `RemoveOldLinksAsync(now?)`: removes records (not files) after `idm_pref_delete_links_after_days` days. `-1` = off.
- Checksum: `SetChecksumAsync` (hex) or `SetChecksumFromTextAsync` (`sha256sum`/`md5sum` format). Verified before the `Completed` state.
- `RefreshLinkAsync(id, url)`: changes the source URL. Segments are kept if the size matches.

## HLS

| Feature | State |
|---|---|
| AES-128 (`EXT-X-KEY`) | Supported |
| SAMPLE-AES, MPEG-TS segments | Supported (decrypts AAC ADTS and H.264 NAL 1/5; see below) |
| SAMPLE-AES, fMP4 (`EXT-X-MAP`) and other methods | Error 184 with a clear message |
| fMP4 (`EXT-X-MAP`) without encryption | Supported; `.mp4` output |
| `EXT-X-BYTERANGE` | Supported (including implicit offsets) |
| Resume | Checkpoint after each segment batch; playlist fingerprint (URL without query) in `ETag` with the `hls:` prefix |
| Live stream (no ENDLIST) | Current window snapshot by default. `winidm_hls_record_live_until_stopped` = true records until stopped |
| Separate audio (`EXT-X-MEDIA`) | Merged with ffmpeg through `MediaMerger`. Without ffmpeg, both files are kept |

**SAMPLE-AES (MPEG-TS)** is interpreted from the specification and implemented in `SampleAes.cs`:
- Video: NAL types 1 and 5 only. The 32 bytes after the NAL header stay clear. Blocks 0, 10, ... of 16 bytes are encrypted (AES-128-CBC), the rest stay clear. The IV is reset for each NAL.
- Audio: the ADTS header stays clear. All full blocks of each frame are encrypted, and the IV is reset for each frame.
- PAT, PMT and other streams are untouched. Byte sizes do not change.

ffmpeg is not bundled; it is found on PATH.

## Import and clipboard (`ImportExport`)

`ImportExport.Parse(text, format)` produces an `ImportResult` (links with an optional file name, rejections with a line number and reason, and a `Truncated` flag). `ImportExport.ExtractUrls(text)` extracts http/https URLs from free text for the smart clipboard.

| Format | Support |
|---|---|
| Plain text (one URL per line, `#` is a comment) | Supported; detected automatically |
| JSON (array of strings, or `url` + `fileName` objects) | Supported; detected automatically. Invalid JSON or a non-array throws `FormatException` |
| CSV with a header (`url`/`link`/`address`/`alamat`, optional name `fileName`/`name`/`nama`) | Supported if the format is requested explicitly or a header is present. The Malay header names are kept as legacy aliases |
| Netscape HTML (bookmarks) | Supported; detected automatically |
| 1DM export text | **Not supported.** Its line specification is missing from report 03; requesting this format throws `NotSupportedException` |

Limits: 10,000 links per import. Duplicates are recorded as rejections. URLs are normalized (`Uri.AbsoluteUri`) and rejected if they contain spaces or control characters. Export is not implemented.

## Evidence: what the tests prove

The tests in `tests/WinIDM.Engine.Tests` (194 tests, all passing in three consecutive runs):

- **Network policy** (`NetworkPolicyTests`, with a `FakeNetworkPolicy` switched while a download runs): metered pause and resume from the checkpoint; offline; Wi-Fi only uses `IsWifiOrEthernet` and starts when allowed; metered with no pause or quota keeps downloading and the byte count is written; unmetered keeps the count at zero; the daily limit stops at the limit, survives an engine restart, and resumes when the day changes; monthly counts reset and persist across instances.
- **HTTPS proxy** (`ProxyTlsTests`): the `https` scheme is built; a host with a scheme is rejected (182); an untrusted certificate fails with 130 before any request; with a test certificate validator (thumbprint), the download through the TLS proxy completes and the proxy receives `GET http://127.0.0.1:...`.
- **SAMPLE-AES** (`SampleAesTests`, with a `SampleAesFixture` that encrypts in test code): round-trip for three seeds; bytes marked clear are unchanged in the fixture; NALs other than 1/5 are not encrypted; ADTS frames only cover full blocks (64 bytes encrypted for two frames of 40 and 47 bytes); repeated NALs are decrypted correctly (IV reset); a wrong key does not restore the payload; non-TS and TS without PAT are rejected with 184; an end-to-end HLS SAMPLE-AES download through the engine produces a TS file identical to the plain source.
- **Mutation**: changing the clear size from 32 to 16 makes 5 SAMPLE-AES tests fail; removing the metered pause restriction makes the metered tests fail. Both mutations were reverted.
- **Import** (`ImportExportTests`): every format, duplicates, the 10,000 limit, invalid URLs, invalid JSON, 1DM rejected, clipboard extraction.
- The existing tests (158) still pass. Two old SAMPLE-AES tests were updated for fMP4 (`EXT-X-MAP`) because SAMPLE-AES MPEG-TS is now supported.

## Evidence: what is NOT proven

- **SAMPLE-AES is not tested against real encoder streams or a reference player.** The 32-byte clear pattern, the encrypted blocks starting at 1:9, the per-NAL IV and the per-frame ADTS CBC are an interpretation. The fixture and the decryptor follow the same interpretation, so the tests prove consistency and the TS mapping, not compliance. Emulation prevention bytes (0x000003) are not handled specially.
- **The network policy has not been tested with real Windows.** The `NetworkPolicyAdapter` (WinRT) has no automated tests and has not been verified on real Wi-Fi, Ethernet or mobile data. The tests use a fake implementation.
- **Quota acts at the block level.** Each segment stream can add up to one block (64 KiB) after the limit, and concurrent segments can add a little more. Counts are written every ~1 s; if the process is killed abruptly, up to ~1 s of counting can be lost.
- **HTTPS proxy** is only tested with HTTP targets and a simple test proxy. HTTPS targets through CONNECT on an HTTPS proxy, commercial proxies, and proxy auth over HTTPS are not tested. Type 2 is not yet exposed in the App UI.
- **Import** is not yet wired into the App UI and there is no export. The 1DM text format is not supported.

## Known limits

- A shared transfer handler (`handler` in the ctor) makes `HostResolver`, the proxy and the TLS validator depend on the caller.
- Refresh link with the same size trusts the new server; the expected checksum remains the final verification.
- Quota is counted for HLS by bytes written to the track, not by raw network bytes.
