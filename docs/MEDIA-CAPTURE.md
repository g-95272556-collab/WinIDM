# Media capture in WinIDM

WinIDM captures media the same way IDM+ does on Android: it watches the network traffic of the built-in
browser instead of asking the user to paste a video URL. This document describes the pipeline, the rules,
the site-specific handling, and what is known not to work yet.

## Pipeline

```
WebView2 (browser tab)
  │  CoreWebView2.WebResourceResponseReceived   (every response, all resource contexts)
  ▼
MediaDetector.Inspect(url, Content-Type, Content-Length, pageUrl)      WinIDM.Browser
  │  MIME / extension / size / per-host user rules
  ▼
BrowserTabViewModel.Media  (ObservableCollection<DetectedMedia>, newest first, max 200)
  │  de-duplicated by MediaCapture.DedupeKey(url)
  ▼
Media panel ("Media (n)")  →  Download button
  │  MediaCapture.NormalizeForDownload(url)         full-file URL (YouTube ranges removed)
  │  MediaCapture.SuggestedFileName(...)            page title / URL name + MIME extension
  │  cookies (CoreWebView2.CookieManager), User-Agent, Referer from the live tab
  ▼
Download engine: HTTP / HLS / DASH
```

Everything is local: the panel only lists what the page actually loaded. No URL is fetched for detection
except when the user opens the quality list of a playlist.

## Detection rules (`MediaDetector`)

| Input | Rule |
|---|---|
| `Content-Type` starting `video/` or `audio/` | media (HTTP) |
| `.m3u8` or `application/vnd.apple.mpegurl`, `application/x-mpegurl` | HLS playlist |
| `.mpd` or `application/dash+xml` | DASH manifest |
| Media extension in the URL path | media (HTTP) |
| `text/`, `image/`, `font/`, `application/json`, `application/xml`, `javascript`, `css` | never media |
| `application/octet-stream` | treated as "no MIME", falls back to the extension |
| Size | ordinary files below 10 KB are ignored; playlists are exempt |

Users can override detection per site (Settings → Browser → media types). An entry like `mp4` adds a type,
`!jpg` ignores one; a deny always wins and per-host rules beat the global list.

Playlists are expanded only when the user asks for the quality list: an HLS master yields one entry per
variant (resolution + bitrate, size estimated from the media playlist), a DASH MPD yields one entry per
representation.

## YouTube

YouTube serves video as `https://<node>.googlevideo.com/videoplayback?...`. The player requests it many times,
each time with a different `range`/`rn`/`rbuf` query parameter. Two rules keep this usable:

1. **De-duplication** — the panel key is the URL with those three range parameters removed, so one video
   appears once instead of dozens of times.
2. **Full-file download** — the Download button removes `range`, `rn` and `rbuf` before handing the URL to the
   engine, which asks the server for the whole file instead of the 1 MB chunk the player requested.

Everything that makes the URL valid is preserved: `sig`, `n`, `expire`, `itag`, `mime`, `id`, `source`,
`sparams`, `ei`, `ip`. The request also carries the browser's User-Agent and Cookies, and the page URL as
Referer.

Result: YouTube **progressive** streams (for example itag 18/22, which the page requests as `mime=video/mp4`)
download as a complete MP4. YouTube's DASH/HLS manifests are detected and listed, but streams protected with
SAMPLE-AES (common for high-resolution YouTube) cannot be decrypted by this build — see the limits below.

## Facebook, Instagram, TikTok, X

These sites deliver progressive MP4s from CDNs (`*.fbcdn.net`, `*.cdninstagram.com`, `*.tiktokcdn.com`,
`*.twimg.com`) behind signed query strings and, for private content, a login cookie. The only rewrite is the
removal of byte-range parameters (`range`, `bytestart`, `byteend`, when their value really is a byte range),
which IDM+ also applies on every host. Every other parameter, the signature included, is kept byte-for-byte —
the URL is edited as a string, never re-serialised, so percent-encoding is untouched. The capture works because
the Download button (and the Quality… window) forwards the tab's cookies, User-Agent, Referer and Origin. Private
or age-restricted posts therefore work only while the tab is signed in.

Cookies and `Authorization` are sent only to the same site as the captured URL (same host or same registrable
domain, e.g. `www.` and `cdn.` of one site). A redirect or an HLS segment on another site receives Referer/Origin
but not the cookies.

## Naming

`MediaCapture.SuggestedFileName` prefers, in order:

1. the server's `Content-Disposition` name (including `filename*=UTF-8''…`), captured from the browser
   response and stored on the panel entry,
2. a `?title=` or `?filename=` query value,
3. the last URL path segment when it already looks like a media file (`clip.mp4`, `song.m4a`),
4. the page title,
5. the media title reported by the detector,
6. a stable fallback (`video-<itag>` for YouTube, `media-<hash>` otherwise).

The MIME type picks the extension when the name has none (`video/mp4`→`.mp4`, `audio/mp4`→`.m4a`,
`audio/x-wav`→`.wav`, HLS→`.m3u8`, DASH→`.mpd`; an unknown `audio/*` type gives `.m4a`). Invalid file-name
characters are replaced and the name is clamped to 120 characters without losing its extension.

## Verified by automated tests

- `tests/WinIDM.Browser.Tests/MediaDetectorTests.cs` — MIME/extension/size rules, playlist expansion.
- `tests/WinIDM.Browser.Tests/MediaCaptureTests.cs` — YouTube range removal, signature preservation,
  de-duplication keys, naming and sanitising.
- `tests/WinIDM.Engine.Tests/CredentialScopeTests.cs` — cookies/credentials stay on the captured site.
- `tests/WinIDM.App.E2E/Tests/BrowserTests.cs` — the real WebView2 browser detects an MP4 served by a local
  fixture server and the Download button produces a byte-identical file.
- `tests/WinIDM.App.E2E/Tests/BrowserMediaSessionTests.cs` — a cookie-gated media file (403 without the page's
  session cookie) downloads byte-identically, both directly (S06B) and through the "Add download" editor (S06C).

## Known limits (honest)

| Limit | Consequence |
|---|---|
| SAMPLE-AES with fMP4 (`encv`/`enca` + `sinf`) | download stops with error 184; HLS SAMPLE-AES with MPEG-TS is not exercised against a real stream |
| DRM (Widevine/PlayReady) | never supported, by design |
| YouTube DASH/HLS + `n` throttling | may download slowly; the URL is valid but the server throttles non-player clients |
| Blob/MSE-only players | the media never appears as a network response, so it cannot be captured |
| Post-download merge of separate audio+video tracks | needs FFmpeg on PATH (the app detects video-only and audio-only entries separately) |
| Live streams (HLS/DASH `#EXT-X-ENDLIST` absent) | the downloader fetches the current window only |

## How this compares with IDM+ on Android

The Android app was inspected (decompiled sources, clean-room behaviour review) to make the Windows capture behave
the same way. The important differences:

| Aspect | IDM+ 20.0 (Android) | WinIDM (this app) |
|---|---|---|
| Trigger | URL-first: hook every WebView load, then **re-fetch** the candidate to read its headers | Reads the real response headers of every request WebView2 already made — no second request, so nothing extra is fetched from the site |
| Body sniffing | Reads the first 256 bytes to promote `#EXTM3U`/DASH and reject HTML | Uses `Content-Type` plus extension; a playlist served as `application/octet-stream` is missed |
| Minimum size | 10 KiB hard floor; anything over 25 MiB is accepted without MIME/extension checks | 10 KiB floor; no large-file bypass |
| Range handling | Strips `range`, `bytestart`, `byteend` everywhere, plus `rn`/`rbuf` on googlevideo | Same rules |
| Cookies/headers | CookieManager + UA + Referer + Origin, strips `Range`/`Accept-Encoding` | Cookies + UA + Referer + Origin from the live tab; cookies only to the same site |
| Naming | `Content-Disposition` → `?title=`/`?filename=` → path → page title → MIME extension → "download" | Same precedence (fallback stem differs: `video-<itag>` / `media-<hash>`) |
| YouTube | **Refused by policy** — sniffing is skipped and a dialog says downloading from YouTube violates its terms | Capture is allowed while the browser can play the stream: progressive MP4 is normalised and downloadable, DRM/SAMPLE-AES streams are not |
| Facebook / Instagram | **Refused by policy**, same dialog | Allowed: signed CDN MP4s download with the tab's cookies |
| TikTok / X / Reddit / Tumblr | Dedicated extractors | Generic response capture (works for their progressive CDN media, no post/slide extraction) |
| Playlists | HLS `EXT-X-KEY` AES-128, `EXT-X-MAP`, byte ranges, TS→MP4 via FFmpeg | Same feature set in the download engine |

The deliberate difference: IDM+ refuses YouTube/Facebook/Instagram outright. WinIDM leaves capture enabled for
streams the browser can already play, without circumventing DRM or player-signature protection, and documents the
cases that cannot work.

## Manual check on a real site

1. Open the browser, play a video for a few seconds, then open **Media**.
2. The entry shows the resolution/bitrate when the page used a playlist.
3. **Download** adds it to the main window; the row shows `HLS`/`DASH`/`HTTP` in the Type column.
4. For YouTube, confirm a single entry appears (not one per range) and that the finished file plays end to end.
