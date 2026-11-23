# App-Registrierung (einmalig, durch den Herausgeber)

**Nutzer von ThixSync müssen hiervon nichts wissen:** Sie legen unter *Geräte* eine Verbindung *Google Drive*, *Microsoft
OneDrive*, *SharePoint*, *Dropbox* oder *Box* an, klicken *Anmelden*, melden sich im Browser bei ihrem Konto an und erlauben
den Zugriff. ThixSync bekommt dabei nie das Passwort zu sehen.

Damit das geht, verlangen Google, Microsoft, Dropbox und Box, dass die **App selbst** einmal bei ihnen bekannt ist (so wie
GoodSync, rclone oder Cyberduck auch). Diese Kennung (Client-ID, bei Google und Box zusätzlich ein Client-Secret) wird beim
Bauen in ThixSync eingebaut – danach gilt sie für alle Nutzer und alle Konten.

Ohne eingebaute Registrierung zeigt ThixSync bei der Anmeldung: *„Für <Typ> ist noch keine App-Registrierung hinterlegt
(Einstellungen › Erweitert).“* Dort kann ein Nutzer (z. B. eine Firma mit eigener Registrierung) auch eigene Werte eintragen;
sie haben Vorrang vor den eingebauten.

So läuft die Anmeldung technisch (für alle vier gleich): OAuth 2.0 *Authorization Code* mit **PKCE (S256)**; der Browser kehrt
zu einer Adresse auf diesem Computer zurück (`http://127.0.0.1:<Port>/`, bei Microsoft `http://localhost:<Port>/`), die ThixSync
für höchstens 5 Minuten öffnet. Der Port ist zufällig – außer bei Dropbox (fest **53917**, siehe unten).

## 1. Google Drive – ca. 10 Minuten

1. <https://console.cloud.google.com> › neues Projekt `ThixSync`.
2. **APIs & Dienste › Bibliothek** › *Google Drive API* › Aktivieren.
3. **Google Auth Platform / OAuth-Zustimmungsbildschirm**: Typ *Extern*, App-Name `ThixSync`, Support-E-Mail, Logo
   (`branding/png/thixsync-128.png`), Startseite und Datenschutzerklärung.
   Bereich (Datenzugriff) hinzufügen: `https://www.googleapis.com/auth/drive`.
4. **Zielgruppe / Veröffentlichungsstatus: „In Produktion“ setzen.** Im Status *Test* laufen Anmeldungen nach 7 Tagen ab
   und nur eingetragene Testnutzer kommen hinein.
5. **Clients › Client erstellen** › Anwendungstyp **Desktop-App**, Name `ThixSync` › Client-ID und Client-Secret kopieren.
   Eine Umleitungs-URI wird bei Desktop-Apps nicht eingetragen (Google erlaubt `http://127.0.0.1` mit jedem Port). Das
   „Secret“ einer Desktop-App ist laut Google kein echtes Geheimnis.

**Wichtig:** `…/auth/drive` ist ein *eingeschränkter* Bereich (voller Zugriff auf Drive – nötig, damit ThixSync auch Dateien
sieht, die nicht von ThixSync stammen). Ohne Google-Prüfung zeigt die Anmeldung „Google hat diese App nicht überprüft“ ›
*Erweitert* › *Weiter zu ThixSync*; das geht für bis zu 100 Nutzer. Für mehr reicht man die App bei Google zur Prüfung ein
(Begründung des Bereichs, Demovideo, jährliche Sicherheitsbewertung „CASA“ durch einen von Google anerkannten Prüfer).

## 2. Microsoft OneDrive und SharePoint (Entra ID + private Microsoft-Konten) – ca. 5 Minuten

**Mit welchem Konto?** Mit dem **Geschäftskonto** der eigenen Firma (Entra-Mandant), das App-Registrierungen anlegen darf –
nicht mit einem privaten Microsoft-Konto. Die Registrierung gehört dann der Firma, gilt aber für alle (Arbeit, andere Firmen,
private Outlook.com-/OneDrive-Konten).

1. <https://entra.microsoft.com> › **Identität › Anwendungen › App-Registrierungen › Neue Registrierung**.
2. Name: `ThixSync`.
   Unterstützte Kontotypen: **Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten**
   (mehrinstanzenfähig + privat; ThixSync meldet über den Mandanten `common` an).
   Umleitungs-URI: Plattform **Öffentlicher Client/nativ (mobil und Desktop)**, Wert `http://localhost` (Microsoft erlaubt
   dazu jeden Port). › *Registrieren*.
3. **API-Berechtigungen** › Berechtigung hinzufügen › Microsoft Graph › *Delegierte Berechtigungen*:
   `Files.ReadWrite.All`, `Sites.ReadWrite.All`, `offline_access`, `User.Read`.
   OneDrive-Verbindungen fragen nur `Files.ReadWrite.All`, `offline_access` und `User.Read` an; SharePoint-Verbindungen
   zusätzlich `Sites.ReadWrite.All` (die Website wird über ihre Adresse gefunden).
   Optional: *Administratorzustimmung für <Organisation> erteilen* – dann fragt in der eigenen Firma niemand mehr.
4. **Authentifizierung** › Erweiterte Einstellungen › *Öffentliche Clientflows zulassen*: **Ja** › Speichern.
5. **Übersicht** › *Anwendungs-ID (Client-ID)* kopieren. Ein Geheimnis wird **nicht** gebraucht.

**„Administratorgenehmigung erforderlich“:** Manche Firmen lassen ihre Nutzer Apps nicht selbst zulassen. ThixSync zeigt dann
diese Adresse, die ein Administrator **dieser** Firma einmal öffnet und *für die Organisation* zustimmt:

`https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id=<Client-ID>&scope=https://graph.microsoft.com/.default&redirect_uri=http://localhost`

## 3. Dropbox – ca. 5 Minuten

1. <https://www.dropbox.com/developers/apps> › **Create app** › *Scoped access* › **Full Dropbox** › Name `ThixSync`.
2. **Settings**:
   - *OAuth 2 › Redirect URIs*: **`http://127.0.0.1:53917/`** hinzufügen – genau so, mit Port und Schrägstrich am Ende.
     Dropbox vergleicht die Adresse exakt (anders als Google, Microsoft und Box erlaubt es keinen beliebigen Port); ThixSync
     öffnet für die Anmeldung deshalb immer Port 53917.
   - *Allow public clients (Implicit Grant & PKCE)*: **Allow**.
   - *App key* kopieren (das ist die Client-ID). Das App-Secret wird **nicht** gebraucht (PKCE).
3. **Permissions**: `account_info.read`, `files.metadata.read`, `files.metadata.write`, `files.content.read`,
   `files.content.write` anhaken › *Submit*.
4. Im Status *Development* können sich bis zu 500 Konten verbinden; danach *Apply for production*.

## 4. Box – ca. 5 Minuten

1. <https://app.box.com/developers/console> › **Create Platform App** › *Custom App* › Authentifizierung
   **User Authentication (OAuth 2.0)** › Name `ThixSync`.
2. **Configuration**:
   - *OAuth 2.0 Redirect URIs*: `http://127.0.0.1` (Box erlaubt dazu jeden Port).
   - *Application Scopes*: **Read all files and folders stored in Box** und **Write all files and folders stored in Box**.
   - *OAuth 2.0 Credentials*: Client-ID und Client-Secret kopieren. Box kennt keine öffentlichen Clients – das Secret wird
     gebraucht (wie bei Google ist es in einer Desktop-App kein echtes Geheimnis).
3. Unternehmenskonten: Erlaubt der Box-Administrator einer Firma nur freigegebene Apps, gibt er ThixSync einmal frei
   (*Admin Console › Integrations › Platform Apps Manager*).

## 5. Einbauen

Die Werte kommen beim Bauen als MSBuild-Eigenschaften in das Programm (`Directory.Build.props` → Assembly-Metadaten →
`AppInfo.BuiltIn…`). Leer gelassen heißt: Für diesen Typ ist keine Registrierung eingebaut.

| Eigenschaft | Repository-Secret (CI) | Woher |
|---|---|---|
| `ThixSyncGoogleClientId` | `THIXSYNC_GOOGLE_CLIENT_ID` | Google, Schritt 5 |
| `ThixSyncGoogleClientSecret` | `THIXSYNC_GOOGLE_CLIENT_SECRET` | Google, Schritt 5 |
| `ThixSyncMicrosoftClientId` | `THIXSYNC_MICROSOFT_CLIENT_ID` | Entra, Schritt 5 |
| `ThixSyncDropboxClientId` | `THIXSYNC_DROPBOX_CLIENT_ID` | Dropbox, *App key* |
| `ThixSyncBoxClientId` | `THIXSYNC_BOX_CLIENT_ID` | Box, *Client ID* |
| `ThixSyncBoxClientSecret` | `THIXSYNC_BOX_CLIENT_SECRET` | Box, *Client Secret* |

- **CI:** GitHub › Repository › *Settings › Secrets and variables › Actions* › die sechs Secrets anlegen.
  `.github/workflows/desktop.yml` reicht sie als Umgebungsvariablen an jeden Build weiter.
- **Lokal:** `secrets.local.props.example` nach `secrets.local.props` kopieren (wird von git ignoriert) und ausfüllen –
  oder die Umgebungsvariablen gleichen Namens setzen.
- **Nutzer mit eigener Registrierung:** *Einstellungen › Erweitert* (`AppSettings.OAuthClients`); leere Felder nehmen die
  eingebauten Werte. Eine Verbindung, die mit einer anderen Registrierung angemeldet wurde, muss neu angemeldet werden.

## 6. Was ThixSync von einer Anmeldung aufbewahrt

Das Geheimnis einer Verbindung liegt geschützt auf diesem Computer (Windows: DPAPI, macOS: Schlüsselbund), wird nie mit der
Cloud synchronisiert und nie protokolliert. Format (JSON):

```json
{ "refresh_token": "…", "client_id": "<Client-ID der Registrierung>", "account": "anna@example.com" }
```

Zugriffstoken leben nur im Speicher. Erneuert ein Dienst das Refresh-Token (Box bei jeder Erneuerung, Microsoft und Dropbox
manchmal), speichert ThixSync das neue sofort. Widerruft der Nutzer den Zugriff, meldet der nächste Lauf „Die Anmeldung ist
abgelaufen oder wurde widerrufen – bitte die Verbindung neu anmelden“.

Löschen geht bei allen vier Diensten in deren Papierkorb (Google Drive, Box: Papierkorb; OneDrive/SharePoint: Papierkorb der
Website; Dropbox: gelöschte Dateien) – nichts ist sofort endgültig weg.
