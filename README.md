<p align="center"><img src="branding/png/thixsync-full.png" alt="ThixSync" width="440"></p>

# ThixSync

**Speicheranalyse für Windows und macOS** – eine interaktive Karte zeigt, was deine Laufwerke und Netzwerkfreigaben füllt,
und jede neue Analyse zeigt, was seit der letzten gewachsen oder geschrumpft ist. Inspiriert von WinDirStat und dem
Speicheranalysator von Synology, im Look der Mat-Familie.

- **Interaktive Karte (Treemap)**: jede Datei ist eine Kachel so groß wie ihr Anteil, Ordner rahmen ihren Inhalt ein, die Farbe
  sagt die Art (Videos, Bilder, Audio, Dokumente, Archive, Programme, Entwicklung, System & Cache, Sonstige). Klick wählt,
  Doppelklick zoomt in den Ordner, der Pfad darüber zoomt wieder hinaus, Rechtsklick öffnet das Menü.
- **Liste daneben in Tabs**: *Ordner* als Baum mit Anteil, Größe und Veränderung, *Größte Dateien*, *Veränderungen* (was seit
  der gewählten früheren Analyse dazukam, wuchs, schrumpfte oder verschwand) und *Dateitypen* (Klick hebt die Art auf der Karte hervor).
  Liste und Karte wählen immer dasselbe.
- **Direkt öffnen und löschen**: Öffnen, im Explorer/Finder zeigen, Pfad kopieren – und Löschen (lokal in den Papierkorb,
  auf einer Freigabe endgültig, mit Rückfrage). Die Karte rechnet sofort neu.
- **Quellen**: Laufwerke, Ordner und **SMB-Freigaben** (`\\nas\daten` bzw. `smb://nas/daten`, optional mit Benutzer und
  Passwort – das Passwort bleibt geschützt auf dem Computer). Beim ersten Start werden die Laufwerke des Computers als Quellen angelegt.
- **Analysen bleiben gespeichert**: je Quelle die letzten 5 … 50 (oder alle). Jede neue Analyse wird mit der vorherigen
  verglichen – Kennzahl „Veränderung“, Färbung „nach Veränderung“ (rot gewachsen, blau geschrumpft) und die Liste der Veränderungen.
- **Automatisch analysieren** (täglich, wöchentlich, monatlich) im Hintergrund, mit einer Mitteilung, was sich verändert hat.
- **MatCloud** (die Cloud der Thix-Apps): Quellen und Analysen sind auf all deinen PCs und Macs gleich – du siehst auch die
  Laufwerke deiner anderen Computer und wie sie wachsen. Ein Laufwerk wird nur auf seinem Computer analysiert, eine Freigabe überall.
- **Tray / Menüleiste**, **Autostart**, **Setup** (Windows) bzw. **App-Bundle + DMG** (macOS), **OTA-Updates** mit optionalem
  **Full-Auto-Update**, hell und dunkel.

| Karte (hell) | Karte (dunkel) |
|---|---|
| ![Karte](docs/images/main-map-light.png) | ![Karte dunkel](docs/images/main-map-dark.png) |

| Färbung nach Veränderung | Quellen | Analysen |
|---|---|---|
| ![Veränderungen](docs/images/main-map-changes-light.png) | ![Quellen](docs/images/main-sources-light.png) | ![Analysen](docs/images/main-analyses-light.png) |

| Quelle (SMB-Freigabe) | Einstellungen | Cloud: im Browser anmelden |
|---|---|---|
| ![Quelle](docs/images/main-source-light.png) | ![Einstellungen](docs/images/main-settings-light.png) | ![Cloud](docs/images/main-cloud-light.png) |

## Bedienung

| Taste / Maus | Wirkung |
|---|---|
| Klick auf eine Kachel oder Zeile | wählen (Karte und Liste zeigen dasselbe) |
| Doppelklick / `Enter` in der Liste | in den Ordner zoomen (eine Datei: öffnen) |
| `Rücktaste` | eine Ebene höher |
| `Entf` (macOS `⌘ + ⌫`) | löschen (mit Rückfrage, abschaltbar) |
| Rechtsklick | Öffnen, Im Explorer/Finder zeigen, Pfad kopieren, Hineinzoomen, Löschen |
| `F5` | die Quelle neu analysieren |
| `→` `←` in der Liste | Ordner auf- / zuklappen |
| `Strg/⌘ + 1 / 2 / 3` | Karte / Quellen / Analysen |
| `Strg/⌘ + N` | neue Quelle |
| `Strg/⌘ + ,` | Einstellungen |
| `Strg/⌘ + B` | Menü ein- / ausklappen |
| `Esc` | zurück zur Karte |

Die Version unten links („Version 1.0.0 · release“) öffnet „Über ThixSync“.

## Installation

**Windows** – `ThixSyncSetup.exe` aus dem [Release-Kanal](https://github.com/Real-TTX/ThixSync/releases/tag/release-channel)
starten. Installiert pro Benutzer nach `%LocalAppData%\Programs\ThixSync` (ohne Administratorrechte), mit Startmenü,
„Installierte Apps“, optional Autostart, Desktop-Verknüpfung und automatischen Updates (vorausgewählt). Auch über den
**ThixLauncher** installierbar (Store der Thix-Apps). Deinstallieren über „Installierte Apps“.

**macOS** – `ThixSync-macOS-arm64.dmg` (Apple Silicon) bzw. `-x64.dmg` (Intel) öffnen und ThixSync in „Programme“ ziehen.
Die App ist (noch) ad hoc signiert: beim ersten Start Rechtsklick › Öffnen. Für geschützte Ordner (Schreibtisch, Dokumente,
Downloads, andere Volumes) fragt macOS einmal; mit „Festplattenvollzugriff“ liest ThixSync alles.

**Cloud** – die Cloud ist **MatCloud**, die gemeinsame Cloud aller Thix-Apps (eigenes Repository `MatCloud`, Docker, Port 4950).
In ThixSync unter Einstellungen › Cloud die Adresse eingeben und **„Im Browser anmelden“** wählen (oder mit Benutzername und
Passwort). Über den ThixLauncher installiert, ist ThixSync gleich angemeldet.

## Versionen, Kanäle, Updates

| Branch | Version | Desktop-Kanal |
|---|---|---|
| `main` | `0.1.<build>-<datum>` (z. B. `0.1.42-20261123`) | `release-channel` + Release `v<version>` |
| `dev` | `nightly-<build>-<datum>` | `nightly-channel` |
| sonst / lokal | `local-<datum>` | – (keine Updates) |

Jede installierte Version prüft beim Start und alle 6 Stunden ihren eigenen Kanal (abschaltbar); mit „Updates automatisch
installieren“ (Full-Auto-Update, bei neuen Installationen an) wird ohne Rückfrage aktualisiert und neu gestartet.

## Für Administratoren

Richtlinien (Wert 1): Windows `HKLM\SOFTWARE\Policies\ThixSync`, macOS Konfigurationsprofil `info.schmoldt.thixsync`.

| Richtlinie | Wirkung |
|---|---|
| `LockConfiguration` | Einstellungen nur lesbar |
| `DisableCloud` | keine Verbindung zur MatCloud |
| `DisableUpdates` | keine Update-Prüfung, keine Installation |
| `DisableDelete` | ThixSync zeigt und öffnet, löscht aber nichts |
| `DisableShares` | keine Netzwerkfreigaben als Quellen |

## Entwicklung

```bash
dotnet build ThixSync.slnx
dotnet test tests/ThixSync.Tests
dotnet run --project src/ThixSync -- --snapshot docs/images   # alle Fenster als PNG (hell/dunkel) mit Beispieldaten
```

- Tests: Scanner, gespeicherte Bäume, Vergleich, Treemap, Quellen mit allen Prüfungen, Löschen (nie das System, nur der
  Testordner), Sync zweier Computer gegen eine MatCloud im Speicher (`FakeCloud`) und die Oberfläche headless gerendert.
- `ThixSync.exe --self-test <bericht.txt>` prüft das veröffentlichte (getrimmte) Programm selbst; die CI führt das bei jedem Windows-Build aus.
- `THIXSYNC_DATA_DIR=<ordner>` startet die App mit eigenem Datenordner (eigene Instanz, berührt nichts Installiertes).
- Logo und Icons: `branding/` ([branding/README.md](branding/README.md)).
- Aufbau und Regeln: [CLAUDE.md](CLAUDE.md).

© Matthias Schmoldt
