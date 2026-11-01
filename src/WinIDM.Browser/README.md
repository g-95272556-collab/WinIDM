# WinIDM.Browser

Browser module: ad blocker (ABP/uBO), hosts, link crawler, user script manager (GM_*),
password manager, and browser data utilities. All logic runs locally. No
telemetry, and no network requests are made except by APIs that are called explicitly.

This project was developed clean-room from the specification `research/02-pelayar-media-privasi.md`.

## Public API

### Ad blocker
- `AdBlocker` (`IAdBlocker`): `LoadFilterList`, `LoadHostsList`, `ShouldBlock(url, pageUrl, ResourceType)`,
  `CosmeticSelectors`, `BuildCosmeticStyleSheet`, `AddWhitelistHost`, `RemoveWhitelistHost`, `MarkBadFilter`, `Stats`.
  - Supported options: `third-party`, `first-party`, resource types (including `subdocument`/`frame`), `domain=`,
    `match-case`, `important`, `denyallow=` (exact hosts only), `badfilter`.
  - `$important` overrides ordinary `@@` exceptions. An exception that is also marked `$important` can cancel it.
  - `#@#` element exceptions are supported (generic and domain-specific).
  - Unsupported rules are dropped and counted in `Stats.UnsupportedRules`.
- `FilterListManager`: public list catalog (`Catalog`, data only), `Lists`, `SetEnabled`, `AddCustomSource`,
  `RemoveCustomSource`, `UpdateAsync` (ETag / If-None-Match, cache on disk), `UpdateDueAsync`,
  `RunScheduleAsync` (scheduled updates), `ApplyTo(IAdBlocker)`.
  - Lists are downloaded only after they are enabled and requested. Downloads must use HTTPS (can be disabled for tests).
  - 16 MB size limit per list.
- `HostsBlocklist`: several named hosts sources (`AddOrReplaceSource`, `SetEnabled`, `RemoveSource`, `Sources`),
  whitelist (`AddWhitelist`, `RemoveWhitelist`, `IsWhitelisted`), `TryBlock(url, pageUrl)`, and statistics
  (`GetStats`, `ResetStats`) with counts per matched domain.

### Link crawler
- `SiteCrawler.RunAsync(CrawlOptions, IProgress<CrawlProgress>?, ct)`: recursion with depth
  (`MaxDepth`, max 10) and page (`MaxPages`, max 10,000) limits, host scope (`CrawlHostScope`: exact host,
  include subdomains, or any host), regex filters (`IncludeRegex` for files, `ExcludeRegex` for pages and files),
  extensions, size (HEAD), and `Last-Modified` date.
  - robots.txt is respected by default (`RobotsTxt`). 4xx errors allow everything; 5xx errors deny everything.
  - Polite delay between requests (`PoliteDelay`, default 1 s). Crawl-delay is honored when larger.
  - One session at a time; a second session throws `InvalidOperationException`.
  - Cancellation returns partial results with `Cancelled = true`.

### Password manager (`PasswordVault`)
- Existing: `SetupAsync`, `UnlockAsync`, `Lock`, `ChangePinAsync`, `AddAsync`, `RemoveAsync`, `GetPasswordAsync`,
  `CheckAutofill`, `ImportCsvAsync`, `ExportRawCsvAsync`.
- New:
  - `ListAllAsync` (origin, username, created/modified date; no passwords) and `SearchAsync`.
  - `EditAsync(origin, username, newOrigin, newUsername, newPassword?)`; the created date is kept.
  - `FindDuplicatePasswordsAsync`: duplicates are detected with a keyed HMAC tag.
  - `AutofillAsync(pageUrl, formActionUrl?)`: HTTPS and same origin only; multiple accounts.
  - `ExportEncryptedAsync(passphrase)` and `ImportEncryptedAsync(json, passphrase)`: PBKDF2-SHA256 (600,000 iterations)
    and AES-256-GCM. Passphrase minimum 12 characters.
  - `RawExportWarning`: mandatory warning text shown before an unencrypted CSV export.
  - `PasswordStrength.Evaluate(password)`: score 0–4, estimated entropy, and warnings (local).
- Lockout: the remainder is stored with a monotonic clock stamp (`Environment.TickCount64`). Wall-clock changes
  within the same boot have no effect. If the monotonic clock resets (reboot), the stored wall time is used.

### User scripts (`UserScriptHost`)
- Installation, preview, SHA-256 pins for `@require` and `@resource`, `Remove`, `SetEnabled`,
  `ApprovePermission`, `RevokePermission`, `GetLog`.
- Updates: `CheckForUpdateAsync` (`@updateURL`, or `@downloadURL` when the update URL is metadata only; compared
  with `UserScriptVersion.Compare`), `ApplyUpdate` (keeps the ID, values, enabled state, and still-declared
  permissions), `RunScheduledUpdateChecksAsync(now)` (once a day per script; `AutoApplyUpdates` defaults to true).
- Frames: `RegisterFrame(frameId, url, incognito, isMainFrame)`, `UnregisterFrame`, `HandleMessage(frameId, json)`.
- `EvaluateWebRequest(url)`: an `Allow`, `Cancel`, or `Redirect` decision from the GM_webRequest rules.
- `CookieStore` (`IUserScriptCookieStore`): cookie storage for GM_cookie. The default is the in-memory store.

## GM bridge

Each script is injected as a single closure by `BuildInjectionScript(id, incognito, frameId)`. Each script's token
is compared with `CryptographicOperations.FixedTimeEquals`.

Message format (page to host):
```
{"winidm":1,"t":"<token>","frame":"<frameId>","id":"<nonce:seq>","api":"<name>","args":[...]}
```
Reply (host to page):
```
{"winidm":1,"id":"<id>","ok":true,"result":<json>}
{"winidm":1,"id":"<id>","ok":false,"error":"<message>"}
```
Menu invocation (host to page): `{"winidm":1,"invoke":"menu","cid":"<cid>"}`.

APIs handled by the host: `getValue`, `setValue`, `deleteValue`, `listValues`, `xhr`, `notify`, `openTab`,
`clipboard`, `download`, `menu`, `log`, `cookieList`, `cookieSet`, `cookieDelete`, `webRequest`.

Behavior changes from the previous version:
- `GM_getValue`, `GM_setValue`, `GM_deleteValue`, and `GM_listValues` are now **synchronous**. The page-side cache is
  filled during injection. Calling `.then()` on `GM_setValue` no longer works.
- Storage quota per script: 1 MB (keys and values).
- `GM_cookie` and `GM_webRequest` require `@grant` and user approval (sensitive permissions).
- Cookies can only be accessed for the same site (registrable domain) as the script page. The `domain` attribute must
  match the target host. Secure cookies require HTTPS.
- `@noframes` scripts are rejected in non-main frames.

## Script storage format

`userscripts.json` is now version 2: it adds `AutoApplyUpdates` (file level) and `LastUpdateCheckUtc` (per script).
v1 files are read with defaults and rewritten as v2 on the next save.

## App integration (not wired up yet)

1. When WebView2 creates or navigates a frame, call `RegisterFrame` with the ID assigned by the app.
2. For each frame `WebMessageReceived`, call `HandleMessage(frameId, json)` and send the reply
   to the same frame.
3. Main frame: `BuildInjectionScript(id, incognito, "main")` via `AddScriptToExecuteOnDocumentCreatedAsync`.
   For iframes, inject with the `frameId` that matches the registration. A `frame` field that does not match is rejected.
4. In `WebResourceRequested`, call `EvaluateWebRequest(url)` and act on the decision.
5. Provide an `IUserScriptCookieStore` based on `CoreWebView2CookieManager` if GM_cookie is needed.

## Known limits

- The URLs in `FilterListManager.Catalog` have not been verified against the internet and their usage licenses have
  not been checked. Verify before release.
- The `ShouldBlock` filter does not know whether an iframe is a `subdocument`. The WebView2 mapping in the app
  (`CoreWebView2WebResourceContext.Document`) needs review for frames.
- `$denyallow` accepts exact hosts only (no wildcard). Rules with wildcards are dropped.
- Vault lockout is an online control. The vault file can be edited by the same user, and the failure count
  is stored in plain text. After a reboot, the remainder is taken from the wall clock (clock changes
  before a reboot can shorten the lockout).
- Secret values in a CSV export live in .NET strings and cannot be zeroed from memory.
- The `GM_getValue` cache is not updated across tabs. Changes from another frame are only visible after
  re-injection.
- Script `@require`/`@resource` sources and updates are only accepted over HTTPS (except `RequireHttps = false`
  for tests).
- `SiteCrawler` is sequential (one request at a time). The concurrency limit is 1 by design.
- The WebView2 cookie adapter and wiring GM_webRequest to `WebResourceRequested` are not built yet (out of scope).
