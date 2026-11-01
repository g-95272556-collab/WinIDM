# WinIDM — UI language change: Malay → English (authoritative brief)

The application UI is currently written in Malay. The user asked for the **application language to be
English**. Every agent working on this task MUST follow this document so the result is consistent.

## Non-negotiable rules

1. **Translate only human-readable text.** Never rename identifiers: class/method/property/field names,
   XAML `x:Name`, resource keys (`x:Key`, `{DynamicResource ...}`), binding paths, command names,
   enum members, JSON/XML field names, setting keys (`Keys.*`, e.g. `app.default_folder`), format IDs
   (`winidm-theme`), archive magic (`1DMCRYPT`), MIME types, extensions, URLs.
2. **Never change code semantics.** No logic edits, no reformatting of unrelated code, no reordering,
   no removed `using` directives. A pure text substitution + comment translation only.
3. **Keep every interpolation placeholder identical**: `{0}`, `{count}`, `{ex.Message}`, `$"..."`
   expressions, `{Binding ...}`, `&#x0a;`, `\n`, `{}{` escapes, `Format` specifiers, `%%`.
4. **Keep XAML markup intact**: attribute order, `{Binding}` strings, styles, grid sizes. Only text
   content / `Text=`, `Content=`, `Header=`, `ToolTip=`, `Title=`, `Watermark=`, `AutomationProperties.Name=`
   values change.
5. **Do not translate**: log messages that are developer diagnostics may stay English; but any string
   that can reach a dialog, tooltip, status bar, list row, menu, or window title MUST be English.
   When in doubt, translate.
6. **Comments and XML doc comments**: translate Malay comments to English too (same meaning, concise).
   This is a code-quality requirement for the finished repository.
7. **Do not touch** `bin/`, `obj/`, `artifacts/` (generated).
8. Keep sentences short; UI text should read like native English product copy, not a literal gloss.
9. Use "you" sparingly in dialogs; prefer imperative labels ("Save", "Remove", "Open folder").
10. Retain British/American consistency: use **American English** spelling (color, behavior, center,
    license, canceled — but keep existing API names untouched).

## Canonical vocabulary (use these EXACT strings)

### Main window chrome
| Malay | English |
|---|---|
| WinIDM | WinIDM (unchanged) |
| Tambah URL | Add URL |
| Mulakan semua | Start all |
| Jeda semua | Pause all |
| Tetapan | Settings |
| Pelayar | Browser |
| Torrent | Torrent |
| Sandaran | Backup |
| Kata laluan | Passwords |
| Semua | All |
| Sedang muat turun | Downloading |
| Dimulakan | Started |
| Aktif | Active |
| Tidak aktif | Inactive |
| Tidak siap | Unfinished |
| Siap | Completed |
| Ralat | Error |
| Fail hilang | Missing files |
| Berjadual | Scheduled |
| Penjadual | Scheduler |
| Penjadual muat turun | Download scheduler |
| Dayakan penjadual | Enable scheduler |
| Masa mula (HH:mm) | Start time (HH:mm) |
| Masa henti (HH:mm) | Stop time (HH:mm) |
| Ulangan | Repeat |
| Had kelajuan (KB/saat, 0 = tiada) | Speed limit (KB/s, 0 = none) |
| Setiap hari | Every day |
| Isnin-Jumaat | Weekdays |
| Hujung minggu | Weekends |
| Simpan | Save |
| Batal | Cancel |
| Tutup | Close |
| OK | OK |
| Tambah | Add |
| Buang | Remove |
| Padam | Delete |
| Cari | Search |
| Buka | Open |
| Buka folder | Open folder |
| Salin pautan | Copy link |
| Sambung semula | Resume |
| Jeda | Pause |
| Muat semula pautan... | Refresh link… |
| Muat semula (muat turun dari awal) | Re-download (from scratch) |
| Pindah ke atas | Move up |
| Pindah ke bawah | Move down |
| Checksum (MD5 / SHA-256) | Checksum (MD5 / SHA-256) |
| Sifat | Properties |
| Buang dari senarai | Remove from list |
| Buang dan padam fail | Remove and delete file |
| Buang yang dipilih | Remove selected |
| Buang yang siap | Remove completed |

### Column headers / row text
| Malay | English |
|---|---|
| Nama | Name |
| Jenis | Type |
| Saiz | Size |
| Kemajuan | Progress |
| Kelajuan | Speed |
| ETA | ETA |
| Status | Status |
| Dalam giliran | Queued |
| Menyambung | Connecting |
| Dijeda | Paused |
| Menggabung | Merging |
| Ralat: | Error: |
| Susunan manual | Manual order |
| Nama (A-Z) | Name (A-Z) |
| Tarikh (terbaru) | Date (newest) |
| Susunan | Sort order |
| Penapis jenis fail | File type filter |
| Cari mengikut nama fail atau URL | Search by file name or URL |

### Window titles (must match exactly — E2E tests look these up)
| Malay | English |
|---|---|
| Tambah muat turun | Add download |
| Tetapan | Settings |
| Pelayar WinIDM | WinIDM Browser |
| Sandaran dan pulihan | Backup and restore |
| Pengurus kata laluan | Password manager |
| PIN pengurus kata laluan | Password manager PIN |
| Tentang WinIDM | About WinIDM |
| Editor tema tersuai | Custom theme editor |
| Halaman tersimpan | Saved pages |
| Penyekat iklan dan penapis | Ad blocker and filters |
| Kebenaran laman | Site permissions |
| Audit kata laluan | Password audit |
| Mod bacaan | Reader mode |
| Pilih kualiti | Choose quality |
| Pintasan halaman utama | Home page shortcuts |
| Pengurus skrip pengguna | User script manager |
| Pemain video WinIDM | WinIDM video player |
| Grabber laman | Site grabber |

### Domain terms
| Malay | English |
|---|---|
| muat turun | download |
| muat turun semula | re-download |
| muat naik | upload |
| pelayar | browser |
| tetapan | settings |
| sandaran / pulihan | backup / restore |
| kata laluan | password |
| peti kata laluan | password vault |
| pengurus kata laluan | password manager |
| penjadual | scheduler |
| bahagian / segmen | segment |
| sambungan | segment (download) / connection (network) — pick by context |
| had kelajuan | speed limit |
| laluan | path |
| folder | folder |
| fail | file |
| pautan | link |
| halaman | page |
| tapak / laman | site |
| penyekat iklan | ad blocker |
| senarai penapis | filter list |
| skrip pengguna | user script |
| kebenaran | permission |
| proksi | proxy |
| sijil | certificate |
| pelayan | server |
| pelayan tiruan | mock server |
| papan keratan | clipboard |
| dulang | tray |
| tema | theme |
| terang | light |
| gelap | dark |
| sistem | system |
| tersuai | custom |
| lalai | default |
| ciri | feature |
| pepijat | bug |
| ujian | test |
| selesai | finished/completed |
| gagal | failed |
| berjaya | succeeded |
| tidak sah | invalid |
| tidak ditemui | not found |
| tidak disokong | not supported |
| kongsi | share |
| simpan | save (verb) / stored (adjective) |
| buang | remove |
| padam | delete (from disk) |
| isih | sort |
| tapis / penapis | filter |
| cari | search |
| tambah | add |
| ubah / sunting | edit |
| buka | open |
| batal | cancel |
| tutup | close |
| kekal | persist |
| percubaan | attempt |
| cuba semula | retry |
| sambung semula | resume |
| jeda | pause |
| henti | stop |
| mula | start |
| sahkan | validate/confirm |
| amaran | warning |
| ralat | error |
| makluman | notification |
| laporan | report |
| jadual | schedule/table (context) |

### Explicitly NOT translated (persisted tokens keep working)
- `ThemePalette.BaseLight` / `BaseDark` become `"Light"` / `"Dark"`, and `ThemeService` choices become
  `"Light"`, `"Dark"`, `"System"`, `"Custom"`. **A compatibility shim must accept the old Malay values**
  (`Terang`, `Gelap`, `Sistem`, `Tersuai`) when reading stored settings or theme JSON, so existing user
  data keeps working. Tests for these are already provided; update them to the English values and add
  one legacy-value test.
- Setting keys, `Keys.*`, registry value names, file names on disk: unchanged.

## Code-comment translation

Translate `///`, `//` and `<!-- -->` comments that are Malay. Keep them short and technical. Do not
translate comment text that quotes a user-facing string that is intentionally persisted.

## Definition of done for each file

- `grep` for Malay words in the file returns nothing except legacy-compatibility literals explicitly
  required above.
- File still compiles (do not run the build; the orchestrator builds once at the end).
- No identifier, key, binding, or placeholder changed.
- Report a one-line summary plus any string you were unsure about.

## Malay detection aid

Common Malay words to search for: `yang, dan, atau, untuk, dengan, tidak, boleh, akan, sudah, pada,
dari, ini, itu, jika, supaya, semasa, selepas, sebelum, juga, hanya, semua, ada, tiada, tetapi, kerana,
dalam, atas, bawah, antara, melalui, oleh, sebagai, telah, masih, perlu, mesti, gagal, berjaya, ralat,
amaran, tetapan, muat, turun, sandaran, kata, laluan, pelayar, halaman, fail, folder, pautan, pilihan,
senarai, butang, tetingkap, pengguna, masa, tarikh, saiz, jenis, nama, status, nilai, kunci, medan,
cari, simpan, buka, tutup, tambah, buang, padam, ubah, pilih, jalankan, hentikan, sambung, jeda`.
