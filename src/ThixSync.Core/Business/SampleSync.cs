using System.Globalization;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business;

/// <summary>
/// Sample data for the pictures of "ThixSync --snapshot" and the UI tests: connections of every group, the four jobs of the mockup
/// ("Dokumente &amp; Fotos", "Projekte NAS", "Fotos Backup", "Server Spiegel"), two weeks of runs with every outcome and the lines of
/// the newest ones, Remote shares and a paired computer, plus a running progress and a plan with conflicts (the engine is not asked).
/// Everything goes through the real services into the data folder of the moment – call it only with a temporary data folder, the
/// clock pinned (<see cref="AppClock"/>) and the token store in memory. Connection types without a provider in this build get one
/// that only says "Beispiel", so that the sample connections count as usable.
/// </summary>
public static class SampleSync
{
    public static readonly Guid ThisComputerId = new("6f1c2a10-0000-4000-8000-000000000001");
    public static readonly Guid ExternalDriveId = new("6f1c2a10-0000-4000-8000-000000000002");
    public static readonly Guid NasId = new("6f1c2a10-0000-4000-8000-000000000003");
    public static readonly Guid WebServerId = new("6f1c2a10-0000-4000-8000-000000000004");
    public static readonly Guid S3Id = new("6f1c2a10-0000-4000-8000-000000000005");
    public static readonly Guid OneDriveId = new("6f1c2a10-0000-4000-8000-000000000006");
    public static readonly Guid GoogleDriveId = new("6f1c2a10-0000-4000-8000-000000000007");
    public static readonly Guid MacBookRemoteId = new("6f1c2a10-0000-4000-8000-000000000008");
    public static readonly Guid MacBookLocalId = new("6f1c2a10-0000-4000-8000-000000000009");
    public static readonly Guid MatCloudId = new("6f1c2a10-0000-4000-8000-00000000000a");

    public static readonly Guid DocumentsJobId = new("7a2d3b20-0000-4000-8000-000000000001");
    public static readonly Guid ProjectsJobId = new("7a2d3b20-0000-4000-8000-000000000002");
    public static readonly Guid PhotosJobId = new("7a2d3b20-0000-4000-8000-000000000003");
    public static readonly Guid ServerJobId = new("7a2d3b20-0000-4000-8000-000000000004");

    /// <summary>The run of "Dokumente &amp; Fotos" of today 10:24 (with many lines: the pages of the protocol).</summary>
    public static readonly Guid DocumentsRunId = new("8b3e4c30-0000-4000-8000-000000000001");

    private const string OtherComputer = "ANNAS-MACBOOK";

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static Random _idSource = new(3);

    /// <summary>Fills the data folder with the sample connections, jobs, runs and Remote settings.</summary>
    public static void Create()
    {
        AddSampleProviders();
        CreateConnections();
        CreateJobs();
        CreateRuns();
        CreateRemote();

        // the analysis of "Projekte NAS" with its open conflicts, kept like after "Analysieren" (details page, resolving)
        JobRunner.KeepPlan(JobService.Get(ProjectsJobId), Plan());
    }

    /// <summary>A folder of the sample computer in the notation of this system ("C:\Users\Anna\Dokumente", "/Users/anna/Documents").</summary>
    public static string LocalFolder(string windowsName, string macName) => OperatingSystem.IsWindows()
        ? $@"C:\Users\Anna\{windowsName}"
        : $"/Users/anna/{macName}";

    /// <summary>"Projekte NAS" (and "Fotos Backup") in the middle of a run: the progress of the running state.</summary>
    public static IReadOnlyList<RunProgress> RunningProgress()
    {
        var mega = 1024.0 * 1024;
        var history = Enumerable.Range(0, 60).Select(second => (38 + 9 * Math.Sin(second / 5.0) + 4 * Math.Sin(second / 1.7) + second / 6.0) * mega).ToList();
        var projects = new RunProgress
        {
            JobId = ProjectsJobId,
            RunId = Guid.NewGuid(),
            Phase = RunPhase.Transferring,
            FilesScannedSource = 8_311,
            FilesScannedTarget = 8_102,
            FilesTotal = 8_311,
            FilesDone = 3_204,
            BytesTotal = (long)(7.9 * 1024 * mega),
            BytesDone = (long)(3.2 * 1024 * mega),
            BytesPerSecond = 48.2 * mega,
            FilesPerSecond = 37.4,
            SpeedHistory = history,
            Workers = 4,
            Elapsed = TimeSpan.FromSeconds(74),
            Remaining = TimeSpan.FromSeconds(98),
            Fraction = 0.41,
            Text = "Kopiere 3.204 von 8.311 Dateien",
            Active =
            [
                new ActiveTransfer("Website/assets/video/intro-4k.mp4", (long)(412 * mega), (long)(286 * mega), PlanAction.CopyToTarget),
                new ActiveTransfer("Kunden/Berger/Entwurf 3.psd", (long)(96 * mega), (long)(23 * mega), PlanAction.CopyToTarget),
                new ActiveTransfer("Website/assets/img/team.jpg", (long)(4.2 * mega), (long)(3.9 * mega), PlanAction.CopyToSource),
                new ActiveTransfer("Buchhaltung/2026/Rechnungen.xlsx", (long)(1.1 * mega), (long)(0.2 * mega), PlanAction.CopyToTarget),
            ],
        };
        var photos = new RunProgress
        {
            JobId = PhotosJobId,
            RunId = Guid.NewGuid(),
            Phase = RunPhase.Transferring,
            FilesScannedSource = 24_870,
            FilesScannedTarget = 24_310,
            FilesTotal = 560,
            FilesDone = 92,
            BytesTotal = (long)(2.3 * 1024 * mega),
            BytesDone = (long)(0.4 * 1024 * mega),
            BytesPerSecond = 12.8 * mega,
            FilesPerSecond = 2.6,
            SpeedHistory = [.. Enumerable.Range(0, 60).Select(second => (11 + 2 * Math.Sin(second / 3.0)) * mega)],
            Workers = 2,
            Elapsed = TimeSpan.FromSeconds(35),
            Remaining = TimeSpan.FromMinutes(2.6),
            Fraction = 0.17,
            Text = "Kopiere 92 von 560 Dateien",
            Active =
            [
                new ActiveTransfer("2026/11/IMG_4821.CR3", (long)(28 * mega), (long)(17 * mega), PlanAction.CopyToTarget),
                new ActiveTransfer("2026/11/IMG_4822.CR3", (long)(27 * mega), (long)(4 * mega), PlanAction.CopyToTarget),
            ],
        };
        return [projects, photos];
    }

    /// <summary>The plan of an analysis of "Projekte NAS": copies both ways, deletions, moves and three conflicts (one resolved).</summary>
    public static SyncPlan Plan()
    {
        var now = AppClock.UtcNow;
        var items = new List<PlanItem>();
        var random = new Random(7);
        FileFacts Facts(long size, int hoursAgo) => new(size, now.AddHours(-hoursAgo), false);

        string[] toTarget =
        [
            "Website/index.html", "Website/kontakt.html", "Website/assets/css/site.css", "Website/assets/js/app.js", "Website/assets/img/hero.webp",
            "Website/assets/video/intro-4k.mp4", "Kunden/Berger/Angebot.docx", "Kunden/Berger/Entwurf 3.psd", "Kunden/Berger/Logo final.ai",
            "Kunden/Schulz/Vertrag 2026.pdf", "Kunden/Schulz/Notizen.md", "Buchhaltung/2026/Rechnungen.xlsx", "Buchhaltung/2026/Belege/Okt.pdf",
            "Buchhaltung/2026/Belege/Nov.pdf", "Präsentationen/Herbst 2026.key", "Präsentationen/Messe/Stand.pptx", "Fotos Shooting/IMG_0412.jpg",
            "Fotos Shooting/IMG_0413.jpg", "Fotos Shooting/IMG_0414.jpg", "Fotos Shooting/IMG_0415.jpg", "Fotos Shooting/IMG_0416.jpg",
            "Fotos Shooting/IMG_0417.jpg", "Fotos Shooting/IMG_0418.jpg", "Fotos Shooting/IMG_0419.jpg", "Texte/Blog/ki-und-backup.md",
            "Texte/Blog/sync-vs-backup.md", "Texte/Newsletter November.docx", "Vorlagen/Brief.dotx", "Vorlagen/Rechnung.xltx", "Code/tools/build.ps1",
            "Code/tools/deploy.sh", "Code/README.md", "Code/src/main.ts", "Code/src/sync.ts", "Code/package.json",
        ];
        foreach (var path in toTarget)
        {
            var size = (long)(random.NextDouble() * random.NextDouble() * 60 * 1024 * 1024) + 2_000;
            items.Add(new PlanItem { Action = PlanAction.CopyToTarget, Path = path, Bytes = size, Source = Facts(size, random.Next(1, 40)), Reason = random.Next(3) == 0 ? "Neu in der Quelle" : "In der Quelle geändert" });
        }

        string[] toSource = ["Kunden/Berger/Feedback.pdf", "Kunden/Schulz/Unterschrift.pdf", "Website/assets/img/team.jpg", "Texte/Korrektur Lektorat.docx", "Scans/Ausweis.pdf", "Scans/Mietvertrag.pdf"];
        foreach (var path in toSource)
        {
            var size = (long)(random.NextDouble() * 8 * 1024 * 1024) + 20_000;
            items.Add(new PlanItem { Action = PlanAction.CopyToSource, Path = path, Bytes = size, Target = Facts(size, random.Next(1, 30)), Reason = "Im Ziel geändert" });
        }

        items.Add(new PlanItem { Action = PlanAction.DeleteOnTarget, Path = "Website/alt/index-alt.html", Target = Facts(18_400, 300), Reason = "In der Quelle gelöscht" });
        items.Add(new PlanItem { Action = PlanAction.DeleteOnTarget, Path = "Website/alt/style-alt.css", Target = Facts(6_100, 300), Reason = "In der Quelle gelöscht" });
        items.Add(new PlanItem { Action = PlanAction.DeleteOnSource, Path = "Scans/Entwurf alt.pdf", Source = Facts(220_000, 500), Reason = "Im Ziel gelöscht" });
        items.Add(new PlanItem { Action = PlanAction.MoveOnTarget, Path = "Archiv/2025/Jahresabschluss.pdf", FromPath = "Buchhaltung/2025/Jahresabschluss.pdf", Reason = "In der Quelle verschoben" });
        items.Add(new PlanItem { Action = PlanAction.MoveOnTarget, Path = "Archiv/2025/Steuer.pdf", FromPath = "Buchhaltung/2025/Steuer.pdf", Reason = "In der Quelle verschoben" });
        items.Add(new PlanItem { Action = PlanAction.CreateFolderOnTarget, Path = "Archiv/2025", IsFolder = true, Reason = "Neu in der Quelle" });
        items.Add(new PlanItem
        {
            Action = PlanAction.Conflict, Path = "Kunden/Berger/Angebot final.docx", Bytes = 182_000,
            Source = Facts(182_000, 2), Target = Facts(176_500, 3), Reason = "Auf beiden Seiten geändert",
        });
        items.Add(new PlanItem
        {
            Action = PlanAction.Conflict, Path = "Buchhaltung/2026/Kassenbuch.xlsx", Bytes = 94_300,
            Source = Facts(94_300, 5), Target = Facts(95_100, 4), Reason = "Auf beiden Seiten geändert",
        });
        items.Add(new PlanItem
        {
            Action = PlanAction.Conflict, Path = "Texte/Blog/entwurf.md", Bytes = 8_200,
            Source = Facts(8_200, 26), Reason = "In der Quelle geändert, im Ziel gelöscht", Resolution = ConflictChoice.KeepSource,
        });

        return new SyncPlan
        {
            JobId = ProjectsJobId,
            CreatedAt = now.AddMinutes(-12),
            Items = items,
            SourceFiles = 8_311,
            TargetFiles = 8_102,
            SourceBytes = 41L * 1024 * 1024 * 1024,
            TargetBytes = 40L * 1024 * 1024 * 1024,
            Skipped = 14,
        };
    }

    private static void AddSampleProviders()
    {
        foreach (var type in ConnectionCatalog.All)
        {
            if (!StorageProviders.IsSupported(type.Kind))
            {
                StorageProviders.OverrideForTests(type.Kind, new SampleProvider(type.Kind));
            }
        }
    }

    private static void CreateConnections()
    {
        Save(new Connection { Id = ThisComputerId, Name = "Annas PC", Kind = ConnectionKind.Local }, null);
        Save(Make(ExternalDriveId, "Backup-Platte", ConnectionKind.ExternalDrive, (ConnectionFields.VolumeLabel, "BACKUP"), (ConnectionFields.VolumeId, "5A1F-22C3")), null);
        Save(Make(NasId, "NAS Wohnzimmer", ConnectionKind.Synology, (ConnectionFields.Host, "diskstation.local"), (ConnectionFields.Protocol, "sftp"),
            (ConnectionFields.UserName, "anna")), "geheim");
        Save(Make(WebServerId, "Webserver", ConnectionKind.Sftp, (ConnectionFields.Host, "server.example.com"), (ConnectionFields.Port, "22"),
            (ConnectionFields.UserName, "deploy"), (ConnectionFields.AuthMethod, "key"), (ConnectionFields.PrivateKeyFile, "~/.ssh/id_ed25519"),
            (ConnectionFields.HostKey, "SHA256:9c4Hq0Lw2V8Yx1bNfK3pZ7tRmA6sJeD5uQ0oG2iT4wE")), null);
        Save(Make(S3Id, "S3 Fotoarchiv", ConnectionKind.S3, (ConnectionFields.Bucket, "anna-fotoarchiv"), (ConnectionFields.Region, "eu-central-1"),
            (ConnectionFields.AccessKeyId, "AKIAIOSFODNN7EXAMPLE")), "geheim");
        Save(Make(OneDriveId, "OneDrive", ConnectionKind.OneDrive, (ConnectionFields.SignedInAs, "anna.berger@outlook.de")), "token");
        Save(Make(GoogleDriveId, "Google Drive (Arbeit)", ConnectionKind.GoogleDrive), null);
        Save(Make(MacBookRemoteId, "Annas MacBook", ConnectionKind.ThixSyncRemote, (ConnectionFields.Host, "annas-macbook.local"),
            (ConnectionFields.RemoteShare, "Dokumente"), (ConnectionFields.RemoteDevice, OtherComputer),
            (ConnectionFields.Fingerprint, "3F:9A:C2:71:0B:5E:D4:88:1A:6C:F0:27:93:BE:44:D1")), "kopplung");
        Save(Make(MatCloudId, "ThixDrive", ConnectionKind.MatCloud), null);

        // a computer of the same account (from the cloud): its own folders, not reachable here
        var now = AppClock.UtcNow;
        ConnectionService.ApplyRemote(new Connection
        {
            Id = MacBookLocalId, Name = "MacBook", Kind = ConnectionKind.Local, DeviceName = OtherComputer, CreateDate = now.AddDays(-30), UpdateDate = now.AddDays(-30),
        });
    }

    private static void CreateJobs()
    {
        var device = AppInfo.MachineName;
        JobService.Save(new SyncJob
        {
            Id = DocumentsJobId,
            Name = "Dokumente & Fotos",
            Description = "Arbeitsdokumente und Familienfotos in OneDrive",
            Mode = SyncMode.TwoWay,
            SourceConnectionId = ThisComputerId,
            TargetConnectionId = OneDriveId,
            Pairs =
            [
                new FolderPair { SourcePath = LocalFolder("Dokumente", "Documents"), TargetPath = "/Dokumente (Backup)" },
                new FolderPair { SourcePath = LocalFolder(@"Bilder\Fotos", "Pictures/Fotos"), TargetPath = "/Fotos (Backup)" },
            ],
            Schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(20, 0) },
            RunsOnDevice = device,
        });
        JobService.Save(new SyncJob
        {
            Id = ProjectsJobId,
            Name = "Projekte NAS",
            Description = "Kundenprojekte mit dem NAS im Wohnzimmer abgleichen",
            Mode = SyncMode.TwoWay,
            SourceConnectionId = ThisComputerId,
            TargetConnectionId = NasId,
            Pairs = [new FolderPair { SourcePath = LocalFolder("Projekte", "Projekte"), TargetPath = "/home/Projekte" }],
            Schedule = new JobSchedule { Kind = ScheduleKind.Interval, IntervalMinutes = 15, RunOnChange = true },
            Options = new JobOptions { Conflicts = ConflictPolicy.Ask, Parallelism = 4 },
            RunsOnDevice = device,
        });
        JobService.Save(new SyncJob
        {
            Id = PhotosJobId,
            Name = "Fotos Backup",
            Description = "Alle Fotos mit Versionen in den S3-Bucket",
            Mode = SyncMode.Backup,
            SourceConnectionId = ThisComputerId,
            TargetConnectionId = S3Id,
            Pairs = [new FolderPair { SourcePath = LocalFolder("Bilder", "Pictures"), TargetPath = "/fotos" }],
            Schedule = new JobSchedule { Kind = ScheduleKind.Weekly, TimeOfDay = new TimeOnly(22, 0) },
            Options = new JobOptions { KeepVersionsDays = 90, VerifyAfterCopy = true },
            RunsOnDevice = device,
        });
        JobService.Save(new SyncJob
        {
            Id = ServerJobId,
            Name = "Server Spiegel",
            Description = "Die Website des Servers auf die Backup-Platte spiegeln",
            Mode = SyncMode.Mirror,
            SourceConnectionId = WebServerId,
            TargetConnectionId = ExternalDriveId,
            Pairs =
            [
                new FolderPair { SourcePath = "/var/www", TargetPath = "/Server/www" },
                new FolderPair { SourcePath = "/etc/nginx", TargetPath = "/Server/nginx" },
            ],
            Schedule = new JobSchedule { Kind = ScheduleKind.Weekly, Days = [DayOfWeek.Sunday], TimeOfDay = new TimeOnly(3, 0) },
            IsPaused = true,
            RunsOnDevice = device,
        });
    }

    private static void CreateRuns()
    {
        var today = AppClock.Today;
        var random = new Random(11);
        _idSource = new Random(3);
        var mega = 1024L * 1024;
        DateTimeOffset At(int daysAgo, int hour, int minute) => new(today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute));

        // Dokumente & Fotos: every evening at 20:00, today by hand at 10:24
        for (var day = 13; day >= 1; day--)
        {
            AddRun(DocumentsJobId, "Dokumente & Fotos", RunTrigger.Schedule, RunOutcome.Success, At(day, 20, 0), random.Next(40, 160),
                random.Next(4, 180), random.Next(0, 6), (long)(random.NextDouble() * 900 * mega) + 20 * mega, 0, 0);
        }

        AddRun(DocumentsJobId, "Dokumente & Fotos", RunTrigger.Manual, RunOutcome.Success, At(0, 10, 24), 96, 240, 3, 1_310 * mega, 0, 0, DocumentsRunId);

        // Projekte NAS: every few hours, the newest one with two conflicts; an analysis afterwards
        for (var day = 13; day >= 0; day--)
        {
            for (var hour = 8; hour <= (day == 0 ? 11 : 20); hour += 3)
            {
                var copied = random.Next(0, 60);
                AddRun(ProjectsJobId, "Projekte NAS", hour == 8 ? RunTrigger.Startup : RunTrigger.Change, RunOutcome.Success, At(day, hour, random.Next(0, 59)),
                    random.Next(8, 70), copied, random.Next(0, 3), copied * (long)(random.NextDouble() * 6 * mega), 0, 0);
            }
        }

        AddRun(ProjectsJobId, "Projekte NAS", RunTrigger.Schedule, RunOutcome.Warnings, At(0, 14, 15), 41, 18, 1, 220 * mega, 2, 0);
        AddRun(ProjectsJobId, "Projekte NAS", RunTrigger.Manual, RunOutcome.Analysed, At(0, 14, 20), 12, 0, 0, 0, 3, 0);

        // Fotos Backup: weekdays at 22:00, yesterday the bucket did not answer
        for (var day = 13; day >= 2; day--)
        {
            var date = today.AddDays(-day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            AddRun(PhotosJobId, "Fotos Backup", RunTrigger.Schedule, RunOutcome.Success, At(day, 22, 0), random.Next(200, 1500),
                random.Next(30, 420), 0, (long)(random.NextDouble() * 6 * 1024 * mega) + 400 * mega, 0, 0);
        }

        AddRun(PhotosJobId, "Fotos Backup", RunTrigger.Schedule, RunOutcome.Failed, At(1, 22, 0), 8, 0, 0, 0, 0, 1);

        // Server Spiegel: on Sundays until it was paused
        AddRun(ServerJobId, "Server Spiegel", RunTrigger.Schedule, RunOutcome.Success, At(11, 3, 0), 380, 1_840, 12, 2_450 * mega, 0, 0);
        AddRun(ServerJobId, "Server Spiegel", RunTrigger.Schedule, RunOutcome.NeedsConfirmation, At(4, 3, 0), 22, 0, 0, 0, 0, 0);
    }

    private static void AddRun(Guid jobId, string jobName, RunTrigger trigger, RunOutcome outcome, DateTimeOffset started, int seconds, long copied, long deleted,
        long bytes, long conflicts, long errors, Guid? id = null)
    {
        if (started > AppClock.Now)
        {
            return;
        }

        var run = new SyncRun
        {
            Id = id ?? NextId(),
            JobId = jobId,
            JobName = jobName,
            Trigger = trigger,
            Outcome = outcome,
            StartedAt = started.ToUniversalTime(),
            FinishedAt = started.ToUniversalTime().AddSeconds(seconds),
            FilesCopied = copied,
            FilesDeleted = deleted,
            FilesMoved = copied > 20 ? copied / 20 : 0,
            FoldersCreated = copied / 30,
            BytesCopied = bytes,
            Conflicts = conflicts,
            Errors = errors,
            FilesScanned = 2 * (copied * 17 + 4_100),
            Summary = Summary(outcome, copied, deleted, bytes, conflicts),
            DeviceName = AppInfo.MachineName,
        };
        SyncStore.SaveRun(run);
        SyncStore.AddEntries(run.Id, Entries(run));
    }

    /// <summary>Ids of the sample runs from a fixed sequence: the same lines in every picture.</summary>
    private static Guid NextId()
    {
        var bytes = new byte[16];
        _idSource.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static string Summary(RunOutcome outcome, long copied, long deleted, long bytes, long conflicts) => outcome switch
    {
        RunOutcome.Failed => "Ziel nicht erreichbar: der Bucket antwortet nicht (Zeitüberschreitung).",
        RunOutcome.NeedsConfirmation => "Angehalten: 1.240 Dateien würden im Ziel gelöscht (mehr als 50 %).",
        RunOutcome.Analysed => $"Analyse: 41 Dateien zu kopieren, {conflicts} Konflikte.",
        RunOutcome.Warnings => $"{copied.ToString("N0", German)} Dateien kopiert ({Size(bytes)}), {conflicts} Konflikte warten auf dich.",
        _ when copied == 0 && deleted == 0 => "Alles aktuell, nichts zu tun.",
        _ => deleted > 0
            ? $"{copied.ToString("N0", German)} Dateien kopiert ({Size(bytes)}), {deleted} gelöscht."
            : $"{copied.ToString("N0", German)} Dateien kopiert ({Size(bytes)}).",
    };

    private static List<RunEntry> Entries(SyncRun run)
    {
        var entries = new List<RunEntry> { new() { At = run.StartedAt, Message = $"Lauf gestartet auf {run.DeviceName}." } };
        if (run.Outcome == RunOutcome.Failed)
        {
            entries.Add(new RunEntry { At = run.StartedAt.AddSeconds(3), Level = RunEntryLevel.Warning, Message = "Ziel antwortet nicht, neuer Versuch (1 von 3)." });
            entries.Add(new RunEntry { At = run.StartedAt.AddSeconds(8), Level = RunEntryLevel.Error, Message = "Ziel nicht erreichbar: Zeitüberschreitung nach 3 Versuchen." });
            return entries;
        }

        string[] folders = ["Projekte/Website", "Rechnungen/2026", "Fotos/Urlaub Juni", "Fotos/Geburtstag", "Verträge", "Vorlagen", "Scans", "Notizen"];
        string[] extensions = [".docx", ".pdf", ".jpg", ".xlsx", ".png", ".md"];
        var random = new Random(BitConverter.ToInt32(run.Id.ToByteArray(), 0));
        var lines = (int)Math.Min(run.FilesCopied, 260);
        var each = lines == 0 ? 0 : run.BytesCopied / lines;
        for (var line = 0; line < lines; line++)
        {
            var path = $"{folders[random.Next(folders.Length)]}/Datei {line + 1:000}{extensions[random.Next(extensions.Length)]}";
            entries.Add(new RunEntry
            {
                At = run.StartedAt.AddSeconds(2 + line * 0.3),
                Action = random.Next(5) == 0 ? PlanAction.CopyToSource : PlanAction.CopyToTarget,
                Path = path,
                Bytes = (long)(each * (0.5 + random.NextDouble())),
                Message = "Kopiert",
            });
        }

        if (run.Conflicts > 0)
        {
            entries.Add(new RunEntry { At = run.FinishedAt ?? run.StartedAt, Level = RunEntryLevel.Warning, Action = PlanAction.Conflict, Path = "Kunden/Berger/Angebot final.docx", Message = "Auf beiden Seiten geändert – wartet auf deine Entscheidung." });
            entries.Add(new RunEntry { At = run.FinishedAt ?? run.StartedAt, Level = RunEntryLevel.Warning, Action = PlanAction.Conflict, Path = "Buchhaltung/2026/Kassenbuch.xlsx", Message = "Auf beiden Seiten geändert – wartet auf deine Entscheidung." });
        }

        if (run.Outcome == RunOutcome.Success && lines > 100)
        {
            entries.Add(new RunEntry { At = run.StartedAt.AddSeconds(20), Level = RunEntryLevel.Warning, Path = "Fotos/Urlaub Juni/IMG_2231.HEIC", Message = "Datei war in Benutzung, beim zweiten Versuch kopiert." });
        }

        entries.Add(new RunEntry { At = run.FinishedAt ?? run.StartedAt, Message = run.Summary });
        return entries;
    }

    private static void CreateRemote() => SettingsStore.Update(settings => settings with
    {
        Remote = settings.Remote with
        {
            IsEnabled = true,
            Shares =
            [
                new RemoteShare { Name = "Dokumente", LocalPath = LocalFolder("Dokumente", "Documents") },
                new RemoteShare { Name = "Fotos", LocalPath = LocalFolder("Bilder", "Pictures"), IsReadOnly = true },
            ],
            Peers =
            [
                new RemotePeer { DeviceName = OtherComputer, KeyId = "k-7Q2M", PairedAt = AppClock.UtcNow.AddDays(-21), LastSeen = AppClock.UtcNow.AddMinutes(-40) },
            ],
        },
    });

    private static string Size(long bytes)
    {
        var gigabytes = bytes / (1024.0 * 1024 * 1024);
        return gigabytes >= 1
            ? gigabytes.ToString("0.0", German) + " GB"
            : (bytes / (1024.0 * 1024)).ToString("0", German) + " MB";
    }

    private static Connection Make(Guid id, string name, ConnectionKind kind, params (string Key, string Value)[] settings) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        Settings = settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal),
    };

    private static void Save(Connection connection, string? secret) => ConnectionService.Save(connection, secret);

    /// <summary>A provider for the pictures: the type counts as built in, opening it only says that it is a sample.</summary>
    private sealed class SampleProvider(ConnectionKind kind) : IStorageProvider
    {
        public IReadOnlyList<ConnectionKind> Kinds { get; } = [kind];

        public IStorage Open(StorageContext context) => throw new NotSupportedException("Eine Beispielverbindung – sie verbindet sich nirgendwohin.");
    }
}
