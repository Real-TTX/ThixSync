# ThixSync – Logo

Eine Treemap – die Karte dessen, was ein Laufwerk füllt – auf einer petrolfarbenen Kachel: der große weiße Block ist der größte
Ordner, die kleineren werden blasser, der **gelbe** Block ist der, der seit der letzten Analyse gewachsen ist. Akzentfarbe
**#0E8074** (weißer Text 4,8 : 1), Verlauf der Kachel **#3DD9BE → #0E8074**, Gelb **#FFC857**, dunkles Petrol **#0B5E55** (Wolke).

| Datei | Detailstufe | Einsatz |
|---|---|---|
| `logo-icon.svg` | voll (ab 48 px): fünf Blöcke, einer gelb | App-Icon (Windows .ico ab 48 px, macOS .icns), Fenster, Über-Seite |
| `logo-small.svg` | reduziert (16–32 px): drei dicke Blöcke, Kachel randlos | Favicon, kleine .ico-Größen, Taskleiste |
| `logo-tray.svg` | einfarbig, ohne Kachel: drei Blöcke | macOS-Menüleiste (Template-Bild, das System färbt es) |
| `logo-cloud.svg` | voll, randlose Kachel mit Wolke | Kachel für Startbildschirme und die MatCloud (auch „maskable“): `png/thixsync-cloud-*.png` |
| `logo-full.svg` | Icon + Wortmarke „Thix**Space**“ | README, Kopfzeilen, Über-Seiten |

`png/` enthält alle gerenderten Größen. Neu erzeugen (nach jeder Änderung an einer SVG):

```
cd tools && npm install && node render-icons.js
```

Das Skript rendert mit dem installierten Microsoft Edge (headless) und schreibt zusätzlich
`src/ThixSync/Assets/*` (thixsync.ico, Tray-Icons, Logos).
Die macOS-Varianten `png/thixsync-mac-*.png` halten Apples Rand um die Kachel ein; `scripts/package-macos.sh` baut daraus das `.icns`.
Das Logo liegt außerdem in der MatCloud (`src/MatCloud/wwwroot/img/apps/thixsync*`) und im ThixLauncher (`Assets/apps/thixsync.png`).
