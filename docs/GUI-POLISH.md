# WinIDM — GUI polish brief (dialogs and secondary windows)

The main window has been redesigned (accent hero band, left navigation rail, dashboard cards, pill status tabs,
card-style list rows). Every other window must look like it belongs to the same product.

**Read `Themes/Common.xaml` first** — it is the design system. Reuse these keys, do not invent parallel ones:

| Key | Use |
|---|---|
| `Card` | main content surface (radius 12, 1px border, padding 16) |
| `SoftCard` | secondary/inner surface (radius 12, alt background) |
| `Divider` | 1px separator border |
| `Heading` | window/section title, 18px SemiBold |
| `Subheading` | 13px SemiBold |
| `SectionLabel` | 10.5px SemiBold, secondary color, for group labels |
| `Muted` | secondary text |
| `PrimaryButton` | the one main action of the window (`Save`, `Add`, `Create`) |
| `SecondaryButton` (also the implicit Button style) | all other actions |
| `DangerButton` | destructive action (`Delete all`, `Remove`) |
| `GhostButton` / `LinkButton` | low-emphasis or inline link-like action |
| `SideNavButton` | navigation-list entries only |
| `TabToggle` | pill toggles (status tabs) |
| `DownloadRow` | list rows |
| `RowProgress` | thin progress bars |

Derived brushes available everywhere: `AccentBrush`, `AccentTextBrush`, `AccentSoftBrush`,
`AccentSoftBorderBrush`, `SurfaceBrush`, `SurfaceAltBrush`, `WindowBackgroundBrush`, `TextPrimaryBrush`,
`TextSecondaryBrush`, `BorderBrush`, `SuccessBrush`, `DangerBrush`, `WarningBrush`, `SuccessSoftBrush`,
`DangerSoftBrush`, `WarningSoftBrush`, `LinkTextBrush`.

## Layout rules for every window

1. **Page header.** At the top of the window content, inside the window background (not inside the Card):
   - a 36–42px rounded accent tile with a Segoe MDL2 Assets glyph (optional but preferred),
   - the window title as `Heading`,
   - one line of `Muted` copy explaining what the window does (max ~90 characters).
   Use `Padding="20,18,20,12"` and 12px spacing between tile and text.
2. **Body.** One `Card` (or a stack of cards separated by 12px) filling the remaining space,
   with `Margin="20,0,20,0"`.
3. **Footer / actions.** A horizontal button row aligned **right**, above the bottom edge, with 8px gaps.
   The primary action uses `PrimaryButton`; Cancel is `SecondaryButton`; destructive actions `DangerButton`.
   `Margin="20,14,20,16"`.
4. **Spacing.** 16px between sections, 10px between a label and its field, 8px between related fields.
   Never let two different cards touch.
5. **Labels above fields.** Setting rows: label (13px, primary) + optional `Muted` help line under it on the left,
   the input (fixed width 180–240) on the right. Align the input column with a `Grid` column, not with spaces.
6. **Lists and grids.** Use the same header/row look as the main window: `GridViewColumnHeader` is already styled,
   rows use `DownloadRow`. Remove any hard-coded light-mode `Background="White"`/`Foreground="Black"`.
7. **Empty states.** Center a muted icon tile + one sentence; never an empty white box.
8. **Window chrome.** Keep `Window` defaults (`Background`, `Foreground`, font come from the implicit style).
   Size windows so the smallest supported size still shows every control.

## Hard constraints (tests depend on these)

- **Do not rename any `ControlType.Button` that has a text label used by automation** — keep the visible text
  exactly as the localization brief defines (for example `Save`, `Cancel`, `Add`, `Close`, `OK`).
- Keep window `Title` values exactly as in `docs/LOCALIZATION-en.md`.
- Keep every `x:Name` used from code-behind: the code-behind references by name and will not compile otherwise.
- Do not change the number/order of `Edit` controls inside `AddUrlWindow` (E2E addresses them by index:
  URL, file name, folder, parts, username, password, referer).
- Do not remove the `Geometry`/`Editor`-driven fields in `AddUrlWindow` that the E2E test fills by index.
- Do not run `dotnet build`/`dotnet test`; the orchestrator builds and runs the suite.

## Definition of done

- The window visually matches the main window's language: cards, rounded corners, accent color, secondary text.
- No leftover default WPF gray-on-white surfaces.
- XAML compiles (balanced tags, all resource keys exist in Common.xaml).
- Report: which files changed and a one-line description of the new layout.
