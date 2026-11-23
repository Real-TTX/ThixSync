# ThixSync – Logo

Zwei Pfeile, die einander auf einem Kreis folgen – Dateien, die auf beiden Seiten gleich gehalten werden – auf einer blauen
Kachel. Akzentfarbe **#1A5FD6** (weißer Text 5,6 : 1), Verlauf der Kachel **#5AA9FF → #1A5FD6**, dunkles Blau **#123F8F** (Wolke).
Die Geometrie wird erzeugt (Kreis, Bögen und Pfeilspitzen auf einem 256er-Raster), damit alle Detailstufen dieselben Pfeile zeigen.

| Datei | Detailstufe | Einsatz |
|---|---|---|
| `logo-icon.svg` | voll (ab 48 px): zwei Pfeile mit Verlaufskachel | App-Icon (Windows .ico ab 48 px, macOS .icns), Fenster, Über-Seite |
| `logo-small.svg` | reduziert (16–32 px): dickere, größere Pfeile, Kachel randlos, einfarbig | Favicon, kleine .ico-Größen, Taskleiste |
| `logo-tray.svg` | einfarbig, ohne Kachel: die zwei Pfeile | macOS-Menüleiste (Template-Bild, das System färbt es) |
| `logo-cloud.svg` | voll, randlose Kachel mit Wolke | Kachel für Startbildschirme und die MatCloud (auch „maskable“): `png/thixsync-cloud-*.png` |
| `logo-full.svg` | Icon + Wortmarke „Thix**Sync**“ | README, Kopfzeilen, Über-Seiten |

`png/` enthält alle gerenderten Größen. Neu erzeugen (nach jeder Änderung an einer SVG):

```
cd tools && npm install && node render-icons.js
```

Das Skript rendert mit dem installierten Microsoft Edge (headless) und schreibt zusätzlich
`src/ThixSync/Assets/*` (thixsync.ico, Tray-Icons, Logos).
Die macOS-Varianten `png/thixsync-mac-*.png` halten Apples Rand um die Kachel ein; `scripts/package-macos.sh` baut daraus das `.icns`.
Das Logo liegt außerdem in der MatCloud (`src/MatCloud/wwwroot/img/apps/thixsync*`) und im ThixLauncher (`Assets/apps/thixsync.png`).
