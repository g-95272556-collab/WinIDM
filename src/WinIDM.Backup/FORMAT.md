# Backup file format: WinIDM and IDM+ compatibility

This document states what is known about the IDM+ backup format, where that knowledge comes from, and what
CANNOT yet be verified without a real sample file exported from IDM+. No "genuine" file was created or treated
as an IDM+ sample. All test files are self-made (see `tests/WinIDM.Backup.Tests/IdmFormatTests.cs`).

## 1. Evidence and confidence level

The evidence source is the Android IDM+ code decompiled by jadx, at `D:\IDM+\decompiled\sources`.
This is a STATIC study: the code was not run, and no device or sample file was used.

| Item | Source (relative to `decompiled\sources`) | Status |
|---|---|---|
| Magic `1DMCRYPT` (8 ASCII bytes) | `i/jc.java` (byte array `{49,68,77,67,82,89,80,84}`) | Known from the code |
| Version `0x0001` (uint16 BE) | `i/jc.java` (`f7687 = {0, 1}`) | Known from the code |
| PBKDF2 iterations uint32 BE, written as 600000 | `i/jc.java` (`ByteBuffer.putInt(600000)`) | Known from the code |
| Iteration limits when reading: 1000 to 1000000 | `i/jc.java` (`m6246`) | Known from the code |
| Salt 16 bytes, nonce 12 bytes | `i/jc.java` (`m6240`, `m6246`) | Known from the code |
| 42-byte header used as GCM AAD (`updateAAD`) | `i/jc.java` | Known from the code |
| KDF PBKDF2-HMAC-SHA256, 32 bytes, UTF-8 password | `i/jc.java` (`m6245`), `acr/browser/lightning/security/Pbkdf2HmacSha256.java` | Known from the code; algorithm equivalent to RFC 8018 |
| AES-256-GCM, 128-bit tag, `AES/GCM/NoPadding` | `i/jc.java`, `i/ic.java` (`doFinal` writes the tag at the end) | Known from the code |
| File layout: `header(42) \|\| ciphertext \|\| tag(16)` | `i/ic.java`, `i/hc.java` | Known from the code |
| The whole ZIP is encrypted as a single stream (`FileOutputStream -> ic -> ZipOutputStream`) | `i/lc.java` lines 80-100, `i/q61.java` | Known from the code |
| Maximum password 1024 characters; invalid UTF-16 rejected | `i/jc.java`, `Pbkdf2HmacSha256.isWellFormedUtf16` | Known from the code |
| ZIP entry names (listed in section 3) | `i/lc.java` (writer), `i/of1.java` (reader) | Known from the code |
| Categories are expressed as integer IDs (for example 15 = browser passwords) | `idm/.../backup/BackupScheduleConfig.java`, `BackupWorker.java` line 66 | Only ID 15 is confirmed from the code; the full mapping is not yet verified |
| Scheduled backups can run without a password | `idm/.../backup/BackupWorker.java` | Known from the code (WinIDM requires this; see section 6) |

The `IdmFormatTests` and `BackupCryptoCompatTests` tests confirm that WinIDM and the independent writer/reader
(built according to the layout above, including PBKDF2 computed manually as in the Java code) are mutually
compatible. This proves consistency with the code study, NOT compatibility with real IDM+ files.

## 2. Container layer (encrypted file)

```
offset  size  field
0       8     magic        "1DMCRYPT"
8       2     version      uint16 BE = 0x0001
10      4     iterations   uint32 BE (600000 when writing)
14      16    salt         random (SecureRandom)
30      12    nonce        random (SecureRandom)
42      N     ciphertext   AES-256-GCM (length = file size - 58)
42+N    16    tag          GCM 128 bit
```

- Key = PBKDF2-HMAC-SHA256(UTF-8 password, salt, iterations, 32 bytes).
- GCM AAD = 42-byte header (excluding the ciphertext or tag).
- The plaintext content is a normal ZIP (`PK\x03\x04`).
- An unencrypted file is a normal ZIP without a header; `IdmBackupInspector` handles both forms.

## 3. ZIP contents

### 3.1 IDM+ entry names (from `i/lc.java` and `i/of1.java`)

- `idm_settings.json` (JSON object), `ui_settings.json`
- `key_browser_menu_strip.json`, `shortcut_icon_<name>`, `page_filename_*`
- `download_info_<id>.json`: the actual content is **XML** (`<idm><downloadInfo version="30257">...`), not JSON
- `key_bookmarks.json`, `key_history.json`, `key_last_saved_tabs_2.json`, `key_passwords.json`
- `key_saved_pages.json`, `key_domain_part_limit.json`, `key_website_limit.json`
- `key_adblock_whitelist2.json`, `key_adblock_filter_sources.json`, `key_adblock_manual_filters.json`, `key_adblock_bad_filters.json`
- `key_drm_video_play_response.json`, `key_disable_context_menu_response.json`, `key_site_settings.json`, `key_domain_to_app_mapping.json`
- `browser_passwords.json` (written by `lc.m6756`; when it is used is not yet verified); `settings.txt` (read-only, legacy format)

### 3.2 WinIDM entry names (`BackupFormat.cs`, `BackupService.cs`)

- `manifest.json` (WinIDM only; not found in the IDM+ code studied), `idm_settings.json` (same name, WinIDM schema: string map)
- `download_info_<id>.json` (JSON `DownloadBackupRecord`; same name but different content from IDM+ XML)
- `bookmarks.json`, `history.json`, `tabs.json`, `passwords.json`, `adblock.json`, `userscripts.json`, `sitesettings.json`

### 3.3 Payload compatibility

**WinIDM is not payload-compatible with IDM+.** The entry names (`key_*.json` versus `bookmarks.json`), the JSON
schema, the download format (XML versus JSON) and the category IDs differ. Consequently (inferred from these
differences, NOT yet tested with a device):
- IDM+ is not expected to restore a WinIDM file, and WinIDM cannot restore IDM+ payloads even though the
  encrypted container can be decrypted.
- `IdmBackupInspector` can DETECT IDM+ entries and report them; it does not restore them.

Making the payloads match requires the schema of each IDM+ entry (including Gson classes and XML that have not been
fully decompiled) and real samples. That is outside the current scope.

## 4. WinIDM changes for container compatibility

Based on the IDM+ code above, `BackupCrypto` has been updated:

1. **Header as AAD.** Previously WinIDM did not use the header as AAD, so IDM+ files would not have passed
   GCM verification. The 42-byte header is now the AAD (same as IDM+).
2. **Iteration range when reading: 1000 to 1000000** (same as the limits in IDM+ code). Writing stays at 600000.
3. **Passwords with unpaired UTF-16 surrogates are rejected** (same as IDM+).

Encrypted files written by WinIDM versions BEFORE this change can no longer be decrypted. There are no such
production files in this repo.

## 5. What CANNOT be verified without a real IDM+ sample

- Actual runtime output: the study is decompiled code (R8/jadx). The byte form emitted by a real device has not
  been observed.
- The JSON schema of each `key_*.json` entry and of `idm_settings.json` (fields, types, case).
- The full XML structure of `download_info_*.json` (only the `<idm><downloadInfo version="30257">` root was read).
- The full IDM+ category ID mapping (only 15 has been identified).
- Whether IDM+ accepts unknown entries (for example WinIDM names) during restore.
- The default file names generated by IDM+ and their extensions.
- The IDM+ version that produced the file (the code studied is one particular R8 build).
- Whether IDM+ accepts an empty password for encrypted files. `i/jc.java` does not reject an empty string,
  but the UI path that sends it is not yet verified.

## 6. How to verify with a real sample

1. In IDM+ (real device), export a backup with a known password, and copy the file to the PC.
2. Run the inspector (no extra code required):
   ```csharp
   var report = IdmBackupInspector.InspectFile(@"C:\copy\sample-idm.zip", "known-password");
   Console.WriteLine(report.ToText());
   ```
3. Check: `Authentication` must read `succeeded`; `version`, `iterations` and the header size must match section 2;
   the entry list must show the names in section 3.1.
4. If `Authentication` fails even with the correct password, the header layout or AAD does not match the study;
   report it as a compatibility issue and keep the 42-byte header for analysis.
5. Only after that, add a golden test with that sample and state its origin in this document.
   Do not store passwords or personal data in the repo.
