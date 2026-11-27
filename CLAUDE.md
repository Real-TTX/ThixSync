# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixSync is

File **synchronisation and backup** for **Windows and macOS** (desktop app with a window, tray / menu bar icon, plus the command line
`thixsync-cli`), functionally like GoodSync: **jobs** keep one or more folder pairs of a **source** and a **target** in step –
two-way, as a mirror or as a backup with versions – with conflict detection, a preview of the changes, schedules, runs on changes,
progress with speed (MB/s and files/s) and a protocol. Source and target are **connections** of 27 types (every type can be both):
this computer, external drives, SMB, NFS, Synology, QNAP, SFTP, FTP/FTPS, WebDAV, rsync over SSH, Hetzner Storage Box, S3 and
S3-compatible, Google Drive, OneDrive, SharePoint, Dropbox, Nextcloud/ownCloud, Backblaze B2, Wasabi, Azure Blob, Google Cloud
Storage, MEGA, pCloud, Box, Seafile, **ThixSync Remote** (PC to PC) and **MatCloud** (ThixDrive). **MatCloud** (sibling repository
`../MatCloud`, app id `sync`, the general sync) keeps jobs and connections (never their secrets) the same on every device.
UI language German; code, comments and identifiers English.

Stack: **.NET 10**, C#. Desktop: **Avalonia 11.3** with **MatDesktop** (the desktop toolkit of MatDesign), SQLite
(Microsoft.Data.Sqlite). Packages: SSH.NET, FluentFTP, MegaApiClient (+ Newtonsoft.Json 13); everything else (S3 SigV4, Azure,
WebDAV, Graph, Drive, Dropbox, Box, pCloud, Seafile, Remote) is own code on HttpClient / SslStream. The scaffolding (setup,
updates, autostart, tray, self test, snapshots, CI) comes from ThixSpace / ThixClip; the app follows MatDesign's rules DESK, TRAY, SETUP.

## Build / run / test

- `dotnet build ThixSync.slnx` – expected: 0 warnings, 0 errors. On Windows the projects target `net10.0-windows10.0.19041.0`,
  elsewhere `net10.0` (`DesktopTargetFramework` in `Directory.Build.props`).
- `dotnet test ThixSync.slnx`:
  - `tests/ThixSync.Tests` (no UI): connections and jobs with every check, the engine against `MemoryStorage` and real folders
    (`EngineTests`, `EngineLocalTests`, `SchedulerTests`), every provider against in-process fakes (`FakeS3`, `FakeWebDavServer`,
    `FakeSeafileServer`, `FakeDrive`, `FakeHttp`, Remote over localhost), the sync of two computers against `FakeCloud`
    (`SyncTests`), the CLI (`CliTests`, `CliSetupTests`), setup/autostart/updates (`PlatformTests`). No internet, no accounts.
  - `tests/ThixSync.UiTests`: the UI rendered headless (every page light/dark, forms, toolbars, pagination, tray, the copies of
    MatCloud's contract and MatDesktop unchanged, no window writes a colour of its own; `THIXSYNC_SCREENSHOTS=<folder>` keeps screenshots).
  Tests never touch real data, the Keychain, the registry or LaunchAgents (`TestData`).
- **Snapshots:** `dotnet run --project src/ThixSync -- --snapshot docs/images` draws every window headless (light, dark, 1x, natural
  size) from `SampleSync` (Core: connections of every group, the jobs "Dokumente & Fotos", "Projekte NAS", "Fotos Backup",
  "Server Spiegel", two weeks of runs, a running progress, a plan with conflicts) with pinned clock 12.11.2026 14:32, version 1.0.0,
  computer ANNA-PC: `setup-*`, `main-{jobs,jobs-running,activity,overview,job-form-<tab>,job-details,job-history,connections,
  connection-types,connection-form,schedules,protocol,run,settings-<tab>,about,folded}`, `toast`, `tray-icon-{windows,macos}[-running]`.
  ThixLauncher's store shows `main-jobs`, `main-activity`, `main-connections` from here. A new page: add it to `Snapshot.cs` (and `FileNames`).
- `ThixSync.exe --self-test <report>` checks the published (trimmed) program; CI runs it after every Windows publish.
- **The MatCloud contract** (`src/ThixSync.Core/MatCloud/`) is a COPY: never edit it here. Change it in MatCloud and copy it again
  with `node ../MatCloud/tools/contract-copy.js src/ThixSync.Core`.
- **MatDesktop** (`src/ThixSync/MatDesktop/`) is a COPY from MatDesign: never edit it here.
- Icons: `cd tools && npm install && node render-icons.js` renders `branding/*.svg` into `branding/png` and `src/ThixSync/Assets`.
- `THIXSYNC_DATA_DIR=<folder>` runs an own instance with its own data (the CLI honours it too).
- **App registrations** of the OAuth clouds: `docs/APP-REGISTRIERUNG.md`; locally `secrets.local.props` (from the `.example`), in CI
  the secrets `THIXSYNC_{GOOGLE,MICROSOFT,DROPBOX,BOX}_CLIENT_ID`, `THIXSYNC_{GOOGLE,BOX}_CLIENT_SECRET`; users may enter their own
  (Einstellungen › Erweitert, `AppSettings.OAuthClients`).
- `scripts/package-macos.sh` builds `ThixSync.app` (+ `Contents/MacOS/thixsync-cli`), the update zip, the dmg and the CLI tar.gz.
- **CLI** `src/ThixSync.Cli` → `thixsync-cli` (never `thixsync`: it would clash with ThixSync.exe, names ignore case): `list`,
  `run <job> [--preview [--all]] [--confirm-deletes] [--json] [--quiet]`, `status`, `runs`, `log <run>`, `connections`, `test`,
  `pause`, `resume`, `export`, `import` (`ExportService`: no secrets, ids kept, existing skipped), `help`, `--version`. Same data
  folder as the app, only the business layer; it never starts the scheduler. Exit codes (`ExitCodes.cs`): 0 ok, 1 warnings/conflicts/
  needs confirmation, 2 failed, 3 usage, 4 not found, 5 business error, 130 Ctrl+C. `scripts/smoke-test-cli.sh <exe>` checks a
  published CLI. On Windows CI embeds the CLI into ThixSync.exe (`-p:ThixSyncCliPath=<file>`); `InstallService` extracts it next to
  ThixSync.exe and – install option `cli-path` – adds the program folder to the user's PATH; uninstall removes both.

## Map

```
src/ThixSync.Core      (no UI) models, business layer (static classes), storages, infrastructure, native code
  MatCloud/            COPY of MatCloud's contract 1.7.0 (CloudApps.Sync, general sync, Drive API, sign-in, CloudHandoff)
  Models/              Connection (+ ConnectionKind, ConnectionGroup, SyncState), SyncJob (FolderPair, SyncMode, JobFilter, JobOptions,
                       JobSchedule, ConflictPolicy), SyncRun (+ RunEntry, RunTrigger, RunOutcome), SyncPlan (PlanItem, PlanAction,
                       ConflictChoice, RunProgress, ActiveTransfer), AppSettings (Remote, OAuthClients, Cloud), InstallerConfig, BusinessException
  Storage/             IStorage (THE contract of every provider: list, get, read, write atomically, folders, delete, move; StorageCapabilities,
                       StorageFeatures, StorageException + StorageErrorKind; ILocalStorage, IChangeFeed), StorageProvider (StorageContext,
                       IStorageProvider – found by reflection, IFolderBrowser, StorageProviders), StoragePath (relative '/' paths),
                       ConnectionCatalog (every type: group, icon, sign-in, the fields with FieldType/FieldTab/VisibleWhen – the UI draws
                       the type picker and the form from it, the business layer validates against it)
    Providers/         Local (this computer, external drives by volume id, LocalStorage = base of every mounted share), Smb, Nfs, Nas
                       (Synology/QNAP → SMB/WebDAV/SFTP), Sftp (SFTP, rsync over SSH with `find -printf` listing, Hetzner), Ftp, WebDav
                       (WebDAV, Nextcloud with X-OC-Mtime), Seafile, S3 (own SigV4; S3, Wasabi, B2, GCS; multipart), Azure, Mega, PCloud,
                       OAuth (PKCE + loopback, token source, CloudApi), GoogleDrive, OneDrive (OneDrive + SharePoint via Graph, delta),
                       Dropbox, Box, Remote (client of ThixSync Remote), MatCloud (ThixDrive), Network (pool, folder walker, TLS trust on
                       first use, HTTP client and error mapping)
  Business/            ConnectionService (CRUD + checks, secrets protected via TokenStore and never synced, Open, TestAsync, ListFoldersAsync,
                       learned values), JobService (CRUD + checks: pairs never overlap, schedule, options; NextRun, Describe*),
                       JobRunner (contract) + Engine/ (Scanner → PathFilter → Planner → Executor with two lanes (small files with full
                       parallelism, large ones limited), ItemOperations, Transfers (OS copy local→local, streams otherwise, hash on the
                       fly), Versions (.thixsync/versions/<run>/…), StateBook + Infrastructure/JobStateStore (state per job, one SQLite
                       per job), TransferMeter (speed history, bytes/s, files/s, ETA), Throttle (global bandwidth), AdaptiveLimiter (AIMD
                       on rate limits), RetryRunner, RunLog, RunSlot/JobQueue/JobLock (MaxConcurrentJobs, lock file per job across
                       processes), ChangeWatchers, PowerStatus), SchedulerService (contract + Engine: schedules, catch-up, start-up,
                       on change, pause all), ProtocolService (runs and lines: query, pages, overview, cleanup), Format (German numbers),
                       OAuthService (sign-in in the browser), CloudSyncService (MatCloud: sign-in, loop push → pull → long poll),
                       CloudSyncItems (collections "connections" and "jobs"), StoreWatcher (changes of other processes, e.g. the CLI),
                       ExportService, SettingsService, AccessPolicy, AutostartService, InstallService, InstallerOptions, UpdateService
                       (the restart says "--tray"; asked for in the window or with the window open, the marker
                       "show-window-after-update" in the update folder brings the window back: ShowWindowAfterUpdate /
                       TakeShowWindowAfterUpdate), MacAppBundle
  Remote/              RemoteServer (contract + implementation: TLS listener, pairing with a code bound to the certificate, device keys,
                       shares, discovery), RemoteSession, RemoteWire, RemoteSecurity, RemoteProtocol (the protocol, documented there)
  Infrastructure/      SyncStore (SQLite sync.db: Connection, Job, Run, RunEntry; tombstones for the cloud; data_version), JobStateStore,
                       SettingsStore, TokenStore (DPAPI / Keychain), CloudClient, AppPaths, AppInfo, AppClock, Log, ProblemReports
  Platform/            NetworkShares (SMB/NFS: Windows WNetAddConnection2, macOS NetFS / mount_nfs), FileSystemActions, WindowActivation,
                       Windows/ShellLink, Windows/EnvironmentChange, Mac/ObjC, Mac/MacTrash
src/ThixSync           (Avalonia app)
  App / Program        start-up, setup mode, single instance, tray, first start (EnsureThisComputer), CloudSyncService, SchedulerService,
                       RemoteServer, OAuthService.OpenBrowser, notifications of finished runs by AppSettings.Notify (TRAY-04); exit stops them
  MainWindow           MatTitleBar (running jobs in the middle: "2 Aufträge · 48,2 MB/s" → Aktivität), MatNav: Synchronisationen, Geräte,
                       Zeitpläne, Protokolle, Einstellungen; foot: cloud state, version = About. Sections plus pages on top (forms, details,
                       a run) with a back stack: Zurück/Esc return to the opener. Keys Ctrl/Cmd+1…4, +, , +N, +B, F5, Esc
  RunMonitor           the running jobs for the UI: JobRunner's progress on the UI thread (throttled to 4/s), starting/stopping runs with
                       every error as a message (also NotImplementedException of an engine that is not there)
  JobStatus            the state word/icon/tone of a job (Aktuell, Läuft …, Fehler, Konflikte, Pausiert, Nie synchronisiert, Bestätigung nötig)
  Pages/               JobsPage (header, tabs Synchronisationen | Aktivität | Übersicht: job cards with toolbar, ActivityView, OverviewView),
                       JobForm (tabs Allgemein | Quelle & Ziel | Filter | Zeitplan | Optionen), JobDetailsPage (Vorschau with the plan,
                       conflicts per row | Verlauf | Einstellungen), ConnectionsPage + ConnectionForm (type picker, then the fields of the
                       catalog in tabs Allgemein | Anmeldung | Erweitert), SchedulesPage, ProtocolPage + RunPage, SettingsPage (Allgemein |
                       Übertragung | Remote | Cloud | Updates | Erweitert), AboutPage
  Controls/            the reusable parts: ListToolbar (search, filters, sort), DataTable<T> (header + rows in a card), Pager, FormFields
                       (errors at fields across tabs), ChoiceCards, ConnectionPicker, FolderPairsEditor, WeekdayPicker, JobCard,
                       ActivityPanel, Sparkline, BarChart, SyncArrow, Spinner, StatePill/StatTile/EmptyState, Formats (de-DE numbers and
                       times), ActionBar, IconLabel, InfoBar, FormErrors
src/ThixSync.Cli` → `thixsync-cli` (never `thixsync`: it would clash with ThixSync.exe, names ignore case): `list`,
  `run <job> [--preview [--all]] [--confirm-deletes] [--json] [--quiet]`, `status`, `runs`, `log <run>`, `connections`, `test`,
  `pause`, `resume`, `export`, `import` (`ExportService`: no secrets, ids kept, existing skipped), `help`, `--version`. Same data
  folder as the app, only the business layer; it never starts the scheduler. Exit codes (`ExitCodes.cs`): 0 ok, 1 warnings/conflicts/
  needs confirmation, 2 failed, 3 usage, 4 not found, 5 business error, 130 Ctrl+C. `scripts/smoke-test-cli.sh <exe>` checks a
  published CLI. On Windows CI embeds the CLI into ThixSync.exe (`-p:ThixSyncCliPath=<file>`); `InstallService` extracts it next to
  ThixSync.exe and – install option `cli-path` – adds the program folder to the user's PATH; uninstall removes both.

## Map

```
src/ThixSync.Core      (no UI) models, business layer (static classes), storages, infrastructure, native code
  MatCloud/            COPY of MatCloud's contract 1.7.0 (CloudApps.Sync, general sync, Drive API, sign-in, CloudHandoff)
  Models/              Connection (+ ConnectionKind, ConnectionGroup, SyncState), SyncJob (FolderPair, SyncMode, JobFilter, JobOptions,
                       JobSchedule, ConflictPolicy), SyncRun (+ RunEntry, RunTrigger, RunOutcome), SyncPlan (PlanItem, PlanAction,
                       ConflictChoice, RunProgress, ActiveTransfer), AppSettings (Remote, OAuthClients, Cloud), InstallerConfig, BusinessException
  Storage/             IStorage (THE contract of every provider: list, get, read, write atomically, folders, delete, move; StorageCapabilities,
                       StorageFeatures, StorageException + StorageErrorKind; ILocalStorage, IChangeFeed), StorageProvider (StorageContext,
                       IStorageProvider – found by reflection, IFolderBrowser, StorageProviders), StoragePath (relative '/' paths),
                       ConnectionCatalog (every type: group, icon, sign-in, the fields with FieldType/FieldTab/VisibleWhen – the UI draws
                       the type picker and the form from it, the business layer validates against it)
    Providers/         Local (this computer, external drives by volume id, LocalStorage = base of every mounted share), Smb, Nfs, Nas
                       (Synology/QNAP → SMB/WebDAV/SFTP), Sftp (SFTP, rsync over SSH with `find -printf` listing, Hetzner), Ftp, WebDav
                       (WebDAV, Nextcloud with X-OC-Mtime), Seafile, S3 (own SigV4; S3, Wasabi, B2, GCS; multipart), Azure, Mega, PCloud,
                       OAuth (PKCE + loopback, token source, CloudApi), GoogleDrive, OneDrive (OneDrive + SharePoint via Graph, delta),
                       Dropbox, Box, Remote (client of ThixSync Remote), MatCloud (ThixDrive), Network (pool, folder walker, TLS trust on
                       first use, HTTP client and error mapping)
  Business/            ConnectionService (CRUD + checks, secrets protected via TokenStore and never synced, Open, TestAsync, ListFoldersAsync,
                       learned values), JobService (CRUD + checks: pairs never overlap, schedule, options; NextRun, Describe*),
                       JobRunner (contract) + Engine/ (Scanner → PathFilter → Planner → Executor with two lanes (small files with full
                       parallelism, large ones limited), ItemOperations, Transfers (OS copy local→local, streams otherwise, hash on the
                       fly), Versions (.thixsync/versions/<run>/…), StateBook + Infrastructure/JobStateStore (state per job, one SQLite
                       per job), TransferMeter (speed history, bytes/s, files/s, ETA), Throttle (global bandwidth), AdaptiveLimiter (AIMD
                       on rate limits), RetryRunner, RunLog, RunSlot/JobQueue/JobLock (MaxConcurrentJobs, lock file per job across
                       processes), ChangeWatchers, PowerStatus), SchedulerService (contract + Engine: schedules, catch-up, start-up,
                       on change, pause all), ProtocolService (runs and lines: query, pages, overview, cleanup), Format (German numbers),
                       OAuthService (sign-in in the browser), CloudSyncService (MatCloud: sign-in, loop push → pull → long poll),
                       CloudSyncItems (collections "connections" and "jobs"), StoreWatcher (changes of other processes, e.g. the CLI),
                       ExportService, SettingsService, AccessPolicy, AutostartService, InstallService, InstallerOptions, UpdateService
                       (the restart says "--tray"; asked for in the window or with the window open, the marker
                       "show-window-after-update" in the update folder brings the window back: ShowWindowAfterUpdate /
                       TakeShowWindowAfterUpdate), MacAppBundle
  Remote/              RemoteServer (contract + implementation: TLS listener, pairing with a code bound to the certificate, device keys,
                       shares, discovery), RemoteSession, RemoteWire, RemoteSecurity, RemoteProtocol (the protocol, documented there)
  Infrastructure/      SyncStore (SQLite sync.db: Connection, Job, Run, RunEntry; tombstones for the cloud; data_version), JobStateStore,
                       SettingsStore, TokenStore (DPAPI / Keychain), CloudClient, AppPaths, AppInfo, AppClock, Log, ProblemReports
  Platform/            NetworkShares (SMB/NFS: Windows WNetAddConnection2, macOS NetFS / mount_nfs), FileSystemActions, WindowActivation,
                       Windows/ShellLink, Windows/EnvironmentChange, Mac/ObjC, Mac/MacTrash
src/ThixSync           (Avalonia app) App / Program, MainWindow (MatTitleBar with the running jobs and their speed, MatNav: Synchronisationen,
                       Geräte, Zeitpläne, Protokolle | Einstellungen; foot: cloud state, version = About), Pages/, Controls/, Tray/, Setup/,
                       Dialogs/, Snapshot.cs, SelfTest.cs
src/ThixSync.Cli       the command line (CliApp, CommandLine, Commands, *Commands, ProgressLine, CliHost, ExitCodes)
tests/ThixSync.Tests   core, engine, providers, Remote, cloud sync, CLI, platform
tests/ThixSync.UiTests the UI headless
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns, no needless nesting.
- **Business layer = static classes.** The UI and the CLI never touch SyncStore, files, registry or HTTP; validation **and permission
  checks** (AccessPolicy: LockConfiguration, DisableCloud, DisableUpdates, DisableDelete, DisableCloudStorage, DisableRemote; "is this
  connection usable on this computer") live in the business layer. Errors are `BusinessException(message, field)`; forms show them at
  the field.
- **Storages:** a new type = an entry in `ConnectionCatalog` + a provider class in `Storage/Providers/<Name>` (public parameterless
  constructor; no list to edit). A storage must be safe for many concurrent calls, stream, write atomically where possible, map every
  error to a `StorageErrorKind`, return the stored entry from `WriteAsync` exactly. Storages without settable times are compared only
  with their own state (the engine handles it). Never log secrets; paths only at debug level.
- **Engine rules:** never delete what is not certain (an unreadable folder is no deletion; a safety limit stops mass deletions; Backup
  never deletes; DisableDelete); the state is written as items finish (cancel and resume work); versions before overwrite/delete.
- **Relational rules** (SQLite): PascalCase tables/columns, key `Id` (64 bit), `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`,
  `UpdateState` (0 deleted, 1 created, 2 updated); `SyncId` is the GUID in the cloud; tombstones until the cloud has them.
- **Sync rules:** general sync of MatCloud, app `sync`; last writer wins by UpdatedAt; secrets never leave the computer; a local
  connection belongs to its computer; a job runs by itself only on `RunsOnDevice`.
- **UI rules (the owner's):** CRUD = list page + create/update page with delete; toolbar above lists (search, filter, sort); actions
  below, left-aligned, positive to negative, delete with a gap; pagination below the table; buttons in one row (Speichern, Zurück …
  Löschen); icons where they help; reusable controls in Controls/; hide what does not apply (the connection form shows only the
  fields of its type); tabs for areas of one item; cards keep their distance; About from the version bottom left. Chrome colours only
  from MatDesktop's theme; ThixSync's accent is `#1A5FD6`.
- **Platforms:** one code base; differences behind `OperatingSystem.IsWindows()/IsMacOS()`. macOS cannot be checked from Windows:
  CI builds and tests it, the owner tries it on a Mac (mounting SMB/NFS, Keychain, folder permissions).
- Edit text files with the editor tools (UTF-8): Windows PowerShell 5.1 `Get-Content`/`Set-Content` double-encode umlauts.

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor. CI sets `Channel`, `BuildNumber` (GitHub run number), `BuildDate`: release
  `0.1.42-20261123` (main), nightly `nightly-43-20261123` (dev), local `local-20261123` (everything else).
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no other
  names (also not in branch names), no "generated with" lines.**
- `.github/workflows/desktop.yml` (every branch; publishing only main/dev): Windows (`dotnet test ThixSync.slnx`, CLI + smoke test,
  win-x64 single file with the CLI embedded = `ThixSyncSetup.exe`, self test, `ThixSync-CLI-win-x64.zip`), macOS (test, CLI + app for
  osx-arm64 + osx-x64, `package-macos.sh` → dmg/zip/CLI tar.gz), publish (manifest `thixsync-update.json` with `installer`, rolling
  releases `release-channel` / `nightly-channel`, versioned release `v<version>` on main).
- MatCloud knows the app id `sync` since contract 1.7.0 (general sync + the Drive as a guest); ThixLauncher lists ThixSync in its
  catalog (`thixsync`, CloudApp Sync, manifest `thixsync-update.json`, `ThixSyncSetup.exe --update`).
