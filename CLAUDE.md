# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixSync is

Disk usage analysis for **Windows and macOS** (desktop app with a window, tray / menu bar icon), like WinDirStat and Synology's
storage analyzer: an **interactive map** (squarified treemap) of what fills a drive, a folder or an **SMB share**; files and folders
open, show in Explorer/Finder and delete right there (local: trash; share: for good). **Sources** and **analyses** are kept; every
new analysis is compared with an earlier one (what grew, shrank, came, went). **MatCloud** (the cloud of all Thix apps, sibling
repository `../MatCloud`, app id `space`, the **general sync**, no module of its own) keeps sources and analyses the same on every
device. UI language German; code, comments and identifiers English.

Stack: **.NET 10**, C#. Desktop: **Avalonia 11.3** with **MatDesktop** (the desktop toolkit of MatDesign: Fluent in the colours of
the tokens, title bar, menu, tray, setup window), SQLite (Microsoft.Data.Sqlite). The cloud speaks the copied contract of MatCloud
(`src/ThixSync.Core/MatCloud/`). The app follows MatDesign's rules DESK, TRAY and SETUP. The scaffolding (setup, updates,
autostart, tray, self test, snapshots, CI) comes from ThixClip.

## Build / run / test

- `dotnet build ThixSync.slnx` – expected: 0 warnings, 0 errors. On Windows the projects target `net10.0-windows10.0.19041.0`,
  elsewhere `net10.0` (`DesktopTargetFramework` in `Directory.Build.props`).
- `dotnet test tests/ThixSync.Tests` – business layer on real folders inside the test's own data folder (`SpaceTests`: scanner,
  stored trees, comparison, treemap, sources with every check, deleting, keep rule, share addresses), the sync of two computers
  against `FakeCloud` (MatCloud's general sync in memory, `SyncTests`) and the **UI rendered headless** (`UiTests`: map/list
  selection, zoom, analyse + delete, the source form, every page light/dark, snapshot, version, tray; `THIXSYNC_SCREENSHOTS=<folder>`
  keeps screenshots). Tests never touch real sources, the Keychain, the registry or LaunchAgents, and the system never opens,
  shows or trashes a file (`FileSystemActions.ForTests`, `TestData`).
- **Snapshots:** `dotnet run --project src/ThixSync -- --snapshot docs/images` draws every window headless (light, dark, 1x, natural
  size) from `SampleSpace` (Anna's C: three times, a NAS share, a MacBook of another device) with pinned clock 12.11.2026 14:32,
  version 1.0.0, computer ANNA-PC: `setup-*`, `main-{map,map-changes,sources,source,analyses,settings,cloud,cloud-code,about,folded}`,
  `toast`, `tray-icon-{windows,macos}[-scanning]`. ThixLauncher's store shows `main-map`, `main-sources`, `main-analyses` from here.
  A new page: add it to `Snapshot.cs` (and `FileNames`).
- `ThixSync.exe --self-test <report>` checks the published (trimmed) program: JSON, SQLite, a real scan of a temporary folder,
  comparison, treemap, token store, COM shortcut, every window. CI runs it after every Windows publish.
- **The MatCloud contract** (`src/ThixSync.Core/MatCloud/`) is a COPY: never edit it here. Change it in MatCloud and copy it again
  with `node ../MatCloud/tools/contract-copy.js src/ThixSync.Core`; `UiTests.The_copy_of_the_MatCloud_contract_is_unchanged` holds it.
- **MatDesktop** (`src/ThixSync/MatDesktop/`) is a COPY from MatDesign (0.2.0): never edit it here; `UiTests.The_copy_of_MatDesktop_is_unchanged`.
- Icons: `cd tools && npm install && node render-icons.js` renders `branding/*.svg` into `branding/png` and `src/ThixSync/Assets`.
- `THIXSYNC_DATA_DIR=<folder>` runs an own instance with its own data. Windows screenshots of the real window need a real screen
  (a disconnected RDP session has 640x480; the map keeps a minimum height of 280 px).
- `scripts/package-macos.sh` builds `ThixSync.app` (Dock icon, folder usage descriptions, ad-hoc signature), the update zip and the dmg;
  with the CLI's publish folder as 6th argument it adds `Contents/MacOS/thixsync-cli` (sharing the app's runtime files) and
  `ThixSync-CLI-macOS-<arch>.tar.gz`.
- **CLI** `src/ThixSync.Cli` → `thixsync-cli` (never `thixsync`: it would clash with ThixSync.exe, names ignore case): `list`, `run
  <job> [--preview [--all]] [--confirm-deletes] [--json] [--quiet]`, `status`, `runs`, `log <run>`, `connections`, `test`, `pause`,
  `resume`, `export`, `import` (ExportService: no secrets, ids kept, existing skipped), `help`, `--version`. Same data folder as the
  app; only the business layer. Exit codes (ExitCodes.cs, in the help): 0 ok, 1 warnings/conflicts/needs confirmation, 2 failed,
  3 usage, 4 not found, 5 business error, 130 Ctrl+C. Seams for tests: `CliHost` (writers, runner, progress, Ctrl+C); `CliTests`.
  `scripts/smoke-test-cli.sh <exe>` checks a published (trimmed) CLI; CI runs it on Windows and macOS.
- **Shipping the CLI on Windows:** CI publishes it (single file, trimmed partial) and embeds it into ThixSync.exe with
  `-p:ThixSyncCliPath=<file>` (resource `thixsync-cli.exe`); `InstallService` extracts it next to ThixSync.exe on install/update and
  – install option `cli-path` (installer.json, `--cli-path=yes|no`, default on) – adds the program folder to the user's PATH
  (HKCU\Environment, WM_SETTINGCHANGE); uninstall removes both (`CliSetupTests` with a test registry key).

## Map

```
src/ThixSync.Core     (no UI) models, business layer (static classes), infrastructure, native code
  MatCloud/            COPY of MatCloud's contract 1.6.0 (CloudApps.Space, the general sync SyncContract, MatCloudClient, sign-in DTOs, CloudHandoff)
  Models/              ScanSource (Local | Smb, DeviceName of a local source, HasPassword), Analysis (numbers of one scan), ScanNode (the tree:
                       Folder | File | Rest = sum of small things; Size, FileCount, FolderCount, children largest first, FullPath, RelativeKey),
                       FileCategories (kinds by extension + the DATA palette of the map, light/dark, and the diverging change colours),
                       AppSettings (+ CloudSettings), BusinessException
  Business/            SourceService (CRUD + every check: name, path exists here, share address, duplicates, policy DisableShares; a local
                       source scans only on its computer; share passwords protected via TokenStore, never synced; OfferDrivesOnce at first start),
                       ScanService (one scan at a time: connect share -> DiskScanner -> AnalysisService.Add; progress, cancel; volume size/free),
                       AnalysisService (list, LoadTree: whole tree in memory for the newest scan of this computer, stored trees pruned to
                       300 000 nodes, cloud trees to 20 000; Previous, Compare, Delete, keep rule), TreeComparison (change per node by
                       path key; Unknown where the earlier tree kept only a sum; TopChanges incl. removed), TreeTools (Prune, Search,
                       LargestFiles, Totals), TreemapLayout (squarified, folders keep a margin, MinimumTileSize 3 px, MaximumTiles, HitTest),
                       FileActionService (Open, Reveal, Delete: real thing, inside the root, not the root, policy DisableDelete; trash locally,
                       for good on a share; takes the node out of the tree), AutoScanService (hourly: sources due by AutoScan), SyncService
                       (sign-in in the browser / password / ThixLauncher handoff; loop push -> pull -> long poll), SpaceSyncItems (collections
                       "sources" and "analyses" as JSON; analysis tree Brotli+Base64 pruned until it fits SyncContract.MaxDataLength),
                       SampleSpace (sample data, temp folders only), SettingsService, AccessPolicy, AutostartService, InstallService,
                       InstallerOptions, UpdateService, MacAppBundle
  Infrastructure/      DiskScanner (FileSystemEnumerable, parallel first levels, no links/junctions, online-only files count 0, skipped folders),
                       TreeCodec (binary + Brotli), SpaceStore (SQLite space.db: Source, Analysis, AnalysisTree; tombstones for the cloud),
                       SettingsStore, TokenStore (DPAPI / Keychain), CloudClient, AppPaths, AppInfo, AppClock, Log, ProblemReports
  Platform/            NetworkShares (parse \\server\share / smb://; Windows WNetAddConnection2; macOS mount_smbfs or Finder), FileSystemActions
                       (open, reveal, SHFileOperation recycle bin / NSFileManager trash), WindowActivation, Windows/ShellLink, Mac/ObjC, Mac/MacTrash
src/ThixSync          (Avalonia app)
  App / Program        start-up, setup mode, single instance, tray, sync, automatic analyses, notifications (TRAY-04)
  MainWindow           MatTitleBar (a running analysis in the middle), MatNav: Karte, Quellen, Analysen, Einstellungen; foot: cloud state, version = About
  Pages/               MapPage (toolbar: source, analysis, comparison, search, kind, colouring; numbers as tiles; tabs Ordner | Größte Dateien |
                       Veränderungen | Dateitypen; the map with path and legend; actions Öffnen, Im Explorer zeigen, Pfad kopieren, Hineinzoomen …
                       Löschen; progress card while scanning), SourcesPage + SourceForm (list with toolbar and actions; form with tabs Allgemein |
                       Analysen, share fields only for a share; Speichern, Zurück … Löschen), AnalysesPage, SettingsPage (Allgemein | Analyse |
                       Cloud | Updates | Erweitert), AboutPage
  Controls/            TreemapView (draws the tiles once into a bitmap, overlay for hover/selection, events NodeSelected / NodeActivated /
                       MenuRequested / HoverChanged), NodeList (virtualised list of nodes in columns, tree mode), DataColors (brushes of the
                       data palette), ActionBar, IconLabel, InfoBar, FormErrors
  Tray/ Setup/ Dialogs/  TrayIcon (analyse a source, cancel, sync), ThixSyncSetup, ConfirmDialog, ProblemReportDialog
tests/ThixSync.Tests  SpaceTests, SyncTests, UiTests, PlatformTests; FakeCloud = MatCloud's general sync in memory
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns.
- **Business layer = static classes.** UI never touches SpaceStore, files, registry or HTTP; validation **and permission checks**
  (AccessPolicy, "is this inside the analysed root", "can this computer scan that source") live in the business layer. Errors are
  `BusinessException(message, field)`; forms show them at the `MatFormField` whose `FormErrors.Field` is that field.
- **Relational rules** (SQLite): PascalCase tables/columns, key `Id` (64 bit), `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`,
  `UpdateState` (0 deleted, 1 created, 2 updated); `SyncId` is the GUID in the cloud; tombstones until the cloud has them.
- **Sync rules:** general sync of MatCloud, app `space`; last writer wins by UpdatedAt; an analysis never changes; deleting a source
  deletes its analyses everywhere; passwords never leave the computer; never log paths of files, passwords or tokens.
- **UI rules:** toolbar above lists (search, filter, sort), actions below, left-aligned, positive to negative, delete with a gap; tabs
  for areas of one item; hide what does not apply (share fields); cards keep their distance; About from the version bottom left.
  **Chrome colours only from MatDesktop's theme** (`Mat.*` resources, `Themed(...)`); the one value ThixSync gives is its accent
  `#0E8074`. The **data colours of the map** are the validated categorical palette in `FileCategories` (Core) – eight hues in a
  fixed order + neutral "Sonstige", a diverging blue ↔ grey ↔ red for changes; identity never by colour alone (legend, tooltip,
  list). `UiTests.No_window_writes_a_colour_of_its_own` holds the UI project to it.
- **Platforms:** one code base; differences behind `OperatingSystem.IsWindows()/IsMacOS()`. macOS cannot be checked from Windows:
  CI builds and tests it, the owner tries it on a Mac (SMB mounting, trash, folder permissions).
- Edit text files with the editor tools (UTF-8): Windows PowerShell 5.1 `Get-Content`/`Set-Content` double-encode umlauts.

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor. CI sets `Channel`, `BuildNumber` (GitHub run number), `BuildDate`: release
  `0.1.42-20261123` (main), nightly `nightly-43-20261123` (dev), local `local-20261123` (everything else).
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no other
  names, no "generated with" lines.**
- `.github/workflows/desktop.yml` (every branch; publishing only main/dev): Windows (`dotnet test ThixSync.slnx`, publish the CLI +
  smoke test, publish win-x64 single file with the CLI embedded = `ThixSyncSetup.exe`, self test, `ThixSync-CLI-win-x64.zip`), macOS
  (test, publish CLI + app for osx-arm64 + osx-x64, `package-macos.sh` → dmg/zip/CLI tar.gz, smoke test), publish (manifest
  `thixsync-update.json` with `installer`, rolling releases `release-channel` / `nightly-channel` incl. the CLI archives, versioned
  release `v<version>` on main). OAuth client ids come from the secrets `THIXSYNC_{GOOGLE,MICROSOFT,DROPBOX,BOX}_CLIENT_ID`,
  `THIXSYNC_{GOOGLE,BOX}_CLIENT_SECRET` as MSBuild properties `ThixSyncGoogleClientId` ….
- MatCloud knows the app id `space` since contract 1.6.0; ThixLauncher lists ThixSync in its catalog (`thixsync`, CloudApp Space).
