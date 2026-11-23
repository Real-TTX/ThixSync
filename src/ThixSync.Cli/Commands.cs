namespace ThixSync.Cli;

/// <summary>Every command of thixsync-cli with its German help (the order is the order of the general help).</summary>
internal static class Commands
{
    private static readonly OptionSpec Json = new("json", "Ausgabe als JSON (für Skripte)");

    public static IReadOnlyList<CommandSpec> All { get; } =
    [
        new("list", "thixsync-cli list [--json]", "Aufträge anzeigen",
            "Zeigt alle Aufträge: Name, Art, Quelle → Ziel (das erste Ordnerpaar), Zeitplan und den letzten Lauf.",
            0, 0, [Json], JobCommands.List),

        new("run", "thixsync-cli run <auftrag> [--preview [--all]] [--confirm-deletes] [--json] [--quiet]", "Auftrag ausführen",
            "Führt einen Auftrag aus und wartet auf das Ende. Im Terminal zeigt eine Zeile den Fortschritt (Phase, Prozent, Dateien, " +
            "Daten, MB/s, Dateien/s, Restzeit); in eine Datei umgeleitet alle paar Sekunden eine Zeile. Strg+C bricht den Lauf sauber " +
            "ab (bereits kopierte Dateien bleiben). Am Ende steht die Zusammenfassung.\n" +
            "Mit --preview wird nur verglichen und der Plan gezeigt (gruppiert, die ersten 20 Schritte; --all zeigt alle) – nichts wird kopiert.",
            1, 1,
            [
                new("preview", "Nur vergleichen und zeigen, was passieren würde", Short: "-p"),
                new("all", "Mit --preview: alle Schritte zeigen"),
                new("confirm-deletes", "Löschungen über der Sicherheitsgrenze des Auftrags bestätigen"),
                Json,
                new("quiet", "Kein Fortschritt, keine Ausgabe außer Fehlern (der Exit-Code sagt alles)", Short: "-q"),
            ],
            JobCommands.Run),

        new("status", "thixsync-cli status [--json]", "Laufende Aufträge",
            "Zeigt die laufenden Aufträge mit ihrem Fortschritt – so weit die Engine sie diesem Programm meldet.",
            0, 0, [Json], ProtocolCommands.Status),

        new("runs", "thixsync-cli runs [--job <auftrag>] [--limit <n>] [--json]", "Letzte Läufe (Protokoll)",
            "Zeigt die letzten Läufe, die neuesten zuerst (Standard: 20). Die Id (die ersten acht Zeichen genügen) öffnet mit " +
            "„thixsync-cli log <id>“ das Protokoll eines Laufs.",
            0, 0,
            [
                new("job", "Nur die Läufe dieses Auftrags", "auftrag"),
                new("limit", "Höchstens so viele Läufe (1 bis 1000)", "n", "-n"),
                Json,
            ],
            ProtocolCommands.Runs),

        new("log", "thixsync-cli log <lauf-id> [--problems] [--json]", "Protokoll eines Laufs",
            "Zeigt einen Lauf und jede Zeile seines Protokolls: was kopiert, gelöscht, verschoben wurde und was warum nicht ging.",
            1, 1, [new("problems", "Nur Warnungen und Fehler"), Json], ProtocolCommands.Log),

        new("connections", "thixsync-cli connections [--json]", "Verbindungen anzeigen",
            "Zeigt alle Verbindungen (Geräte, Server, Cloud-Speicher) mit Adresse und ob dieser Computer sie nutzen kann. " +
            "Kennwörter und Anmeldungen zeigt ThixSync nie.",
            0, 0, [Json], ConnectionCommands.List),

        new("test", "thixsync-cli test <verbindung> [--json]", "Verbindung testen",
            "Verbindet sich mit einer Verbindung und liest ihren Basisordner – wie „Verbindung testen“ in der App.",
            1, 1, [Json], ConnectionCommands.Test),

        new("pause", "thixsync-cli pause <auftrag>", "Automatische Läufe pausieren",
            "Ein pausierter Auftrag läuft nicht mehr von selbst (Zeitplan, Änderungen, Start); von Hand und mit run weiterhin.",
            1, 1, [], JobCommands.Pause),

        new("resume", "thixsync-cli resume <auftrag>", "Automatische Läufe fortsetzen",
            "Hebt die Pause eines Auftrags auf: er läuft wieder nach seinem Zeitplan.",
            1, 1, [], JobCommands.Resume),

        new("export", "thixsync-cli export [datei]", "Aufträge und Verbindungen exportieren",
            "Schreibt alle Aufträge und Verbindungen als JSON in die Datei (ohne Datei oder mit „-“ auf die Ausgabe). " +
            "Kennwörter, Schlüssel und Anmeldungen bleiben auf diesem Computer – nach dem Import sind sie neu einzugeben.",
            0, 1, [], TransferCommands.Export),

        new("import", "thixsync-cli import <datei> [--json]", "Aufträge und Verbindungen importieren",
            "Liest einen Export („-“: von der Eingabe). Die Ids bleiben: was es hier schon gibt (gleiche Id), wird übersprungen und nie " +
            "überschrieben – zweimal importieren ändert nichts. „Dieser Computer“, dasselbe Laufwerk und eine Verbindung gleichen Namens " +
            "und Typs gelten als dieselbe; die Aufträge nutzen dann die hiesige. Was eine Prüfung ablehnt (Name vergeben, Ordner hier " +
            "ungültig, Richtlinie), wird gemeldet und ausgelassen (Exit-Code 5).",
            1, 1, [Json], TransferCommands.Import),
    ];
}
