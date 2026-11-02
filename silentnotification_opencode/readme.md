# opencode-tray

Icona nella **notification area di Windows** (la tendina delle icone nascoste, la freccia `^`
in basso a destra) che mostra a colpo d'occhio cosa sta facendo opencode.

| Stato opencode | Icona | Colore |
| --- | --- | --- |
| sta lavorando (modello o tool in corso) | `O` con quadrato spezzato | **verde** |
| serve una tua risposta (domanda, permissione, errore, retry) | `O` con quadrato spezzato | **gialla** |
| ha finito di scrivere, aspetta il tuo prossimo messaggio | `O` con quadrato spezzato | **nera** |

La componente "strana" dentro la `O` è un quadrato dal contorno interrotto.

**Stealth di default**: nessun popup, nessun suono, nessuna animazione, nessuna finestra.
Il segnale è **solo il colore dell'icona**. Le altre opzioni si attivano dal menu.

---

## Requisiti

- Windows 10 / 11 (usa solo il .NET Framework 4.x, già presente su ogni Windows)
- opencode installato con lo script ufficiale:
  ```sh
  curl -fsSL https://opencode.ai/v2/install | bash
  ```
  (il plugin viene copiato in `~/.config/opencode/plugins/`)
- Nessun diritto di amministratore richiesto: tutto in `HKCU` e `%LOCALAPPDATA%`
- **Linux (GNOME)**: estensione Shell + listener Python; richiede `python3`, `gnome-extensions` e sessione GNOME attiva

## Installazione

Doppio click su `Install.cmd`, oppure:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

Opzioni utili:

```powershell
# cambiare porta o disattivare l'avvio automatico
powershell -ExecutionPolicy Bypass -File install.ps1 -Port 47110 -NoStartWithWindows

# installare solo l'app, senza il plugin opencode
powershell -ExecutionPolicy Bypass -File install.ps1 -NoPlugin

# riscrivere le impostazioni con i valori predefiniti
powershell -ExecutionPolicy Bypass -File install.ps1 -Force
```

L'installer:

1. compila `OpencodeTray.exe` (sorgente in `src/`, nessuna dipendenza esterna);
2. lo installa in `%LOCALAPPDATA%\Programs\opencode-tray`;
3. scrive le impostazioni in `%APPDATA%\opencode-tray\settings.json`;
4. crea la scorciatoia nel menu Start e (opzionale) quella di avvio automatico;
5. registra la voce in **Programmi e caratteristiche** (con disinstallatore);
6. installa il plugin `opencode-tray.js` in `~/.config/opencode/plugins/`;
7. avvia l'app.

> **Dopo l'installazione riavvia opencode**: la configurazione, come tutti i plugin, viene
> caricata una sola volta all'avvio.

## Installazione Linux (GNOME)

Il componente tray su Linux è un'**estensione GNOME Shell** (indicatore nella panel) + un
**listener Python** che ascolta lo stesso HTTP POST del plugin `opencode-tray.js`.
Il plugin JS non cambia: è lo stesso `plugin/opencode-tray.js` usato su Windows.

### Comandi rapidi

```bash
# da questa cartella
bash install-linux.sh
```

Lo script:

1. copia `gnome-extension/` in `~/.local/share/gnome-shell/extensions/opencode-tray@local/`;
2. copia `plugin/opencode-tray.js` in `~/.config/opencode/plugins/` (se presente);
3. scrive `~/.config/opencode-tray/settings.json` (porta `47110`, default come Windows);
4. crea il file di autostart `~/.config/autostart/opencode-tray.desktop`;
5. avvia il listener in background (`python3 listener/opencode-tray-listener.py`);
6. abilita l'estensione (se `gnome-extensions` è disponibile).

Poi **ricarica GNOME Shell** (`Alt+F2` → `r` su X11; su Wayland disconnetti / rientra) e attiva
l'estensione da **GNOME Tweaks / Extensions** (o `gsettings`).

### Riavvio / avvio manuale

```bash
bash linux-start.sh
```

Questo controlla se il listener è già su `47110`; se no lo avvia (`nohup`) e abilita
l'estensione. Per disattivare invece:

```bash
gnome-extensions disable opencode-tray@local
# o rimuovi la cartella dell'estensione:
rm -rf ~/.local/share/gnome-shell/extensions/opencode-tray@local
```

### Percorsi Linux

| Componente | Path |
| --- | --- |
| Estensione GNOME | `~/.local/share/gnome-shell/extensions/opencode-tray@local/` |
| Listener | `.../listener/opencode-tray-listener.py` |
| Stato atomico (tmp) | `/tmp/opencode-tray-state.json` |
| Plugin opencode | `~/.config/opencode/plugins/opencode-tray.js` |
| Impostazioni | `~/.config/opencode-tray/settings.json` |
| Log listener | `/tmp/opencode-tray-listener.log` |

Per dettagli sull'indice dell'indicatore (sinistra dell'orologio) o sulla finestra costi,
vedi `gnome-extension/README.md`.

## Uso

**Windows**: l'icona sta nella tendina delle icone nascoste. Se non la vedi, trascinala fuori dalla
freccia `^` in basso a destra per fissarla nella barra (Windows lo ricorda).

**Linux**: l'indicatore è nella panel (a sinistra dell'orologio, per default `index: 0` in
`settings.json`). Se non compare dopo `Alt+F2 r`, controlla che l'estensione sia attiva
(GNOME Tweaks / Extensions) e che il listener sia in esecuzione (`ps aux | grep listener`).
- **Clic sinistro** sull'icona = menu (stato, costi, progetti, toggle, exit).
- Il menu e gli stati sono gli stessi di Windows (nero / verde / giallo); la finestra costi
si apre con `cost-info-window.py` (vedi `gnome-extension/README.md`).
- **Doppio click** sull'icona = menu:

| Voce | Effetto |
| --- | --- |
| stato: … | stato corrente + numero di istanze opencode attive |
| opencode: ca. …$ | stima del costo (quanto pagheresti se il modello non fosse gratis) |
| Istanze opencode (N) | elenco dei progetti: quale sta lavorando, quale è bloccato in attesa |
| Azzera conteggio costi | azzera il conteggio dei costi (la stima riparte da zero) |
| Info costi (modello - token) | finestra con la stima per progetto/modello e la proiezione settimanale |
| Popup di notifica | **disattivato** di default; se attivo compare il balloon "attenzione" |
| Suono di avviso | **disattivato** di default |
| Icona lampeggiante in attesa | **disattivato** di default; se attivo lampeggia in giallo per 7 secondi |
| Avvia con Windows | crea/rimuove la scorciatoia nella cartella Startup |
| Apri configurazione opencode / installazione / configurazione tray | apre le cartelle |
| Esci | chiude l'app |

### La stima dei costi

- La stima risponde a una domanda sola: **quanto pagherei se il modello non fosse gratis**.
- I modelli gratuiti (es. `minimax-m3.1-flash-free`) vengono valutati con il listino del
  gemello a pagamento più vicino (es. `minimax-m3`): la finestra mostra sempre
  `prezzo di <modello>` quando la stima non usa il listino del modello stesso.
- I prezzi vengono letti dal registry locale di opencode (`~/.cache/opencode/models.json`)
  e ricaricati quando opencode lo aggiorna.
- **Nella stima entrano solo i token dei provider di opencode** (`opencode`, `opencode-go`):
  i token passati da **openrouter** (o da altri provider) sono esclusi, perché hanno un
  listino diverso.
- La finestra "Info costi" mostra, per ogni progetto, i token e il costo per modello, il
  totale di oggi/7 giorni e una **proiezione settimanale e mensile** calcolata sulla media
  giornaliera osservata.
- I costi sono attribuiti a **sessione** (non al processo): ogni sessione opencode viene
  seguita separatamente e i token sono sommati per progetto, quindi due opencode sullo
  stesso progetto non si contano due volte e un opencode chiuso non fa sparire il totale.

Le impostazioni si possono anche scrivere a mano in
`%APPDATA%\opencode-tray\settings.json` (Windows) o `~/.config/opencode-tray/settings.json` (Linux):

```json
{
  "port": 47110,
  "balloon": false,
  "sound": false,
  "flash": false,
  "heartbeatTimeoutSec": 25,
  "startWithWindows": true
}
```

`heartbeatTimeoutSec` è quanto tempo l'icona resta colorata dopo l'ultimo segnale di opencode:
scaduto il timeout (opencode chiuso) l'icona torna nera.

## Disinstallazione

Doppio click su `Uninstall.cmd`, oppure:

- **Programmi e caratteristiche** → *Opencode Tray* → *Disinstalla*
- oppure dalla cartella di installazione:

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

Vengono rimossi processo, scorciatoie (Start e Startup), voce di registro, cartella di
installazione, impostazioni e plugin opencode. Per tenere qualcosa:

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1 -KeepPlugin -KeepSettings
```

Dopo la disinstallazione **riavvia opencode**, altrimenti il plugin resta caricato nella
sessione corrente (e invia solo POST falliti, ignorati).

### Disinstallazione Linux

```bash
# disattiva estensione e rimuovi listener/autostart
rm -rf ~/.local/share/gnome-shell/extensions/opencode-tray@local
rm -rf ~/.config/autostart/opencode-tray.desktop
# opzione: rimuovi anche plugin e impostazioni
rm -rf ~/.config/opencode/plugins/opencode-tray.js
rm -rf ~/.config/opencode-tray
# uccidi listener se ancora attivo
kill $(pgrep -f 'opencode-tray-listener.py') 2>/dev/null || true
```

Poi ricarica GNOME Shell (`Alt+F2 r`) se vuoi rimuovere l'indicatore immediatamente.

## Come funziona

```
opencode  --(eventi)-->  plugin opencode-tray.js
                             |
                             |  HTTP POST http://127.0.0.1:47110/state
                             |  { state, reason, cost, models, sessions, pid, project, cwd, ts }
                             v
                      OpencodeTray.exe  --(icona + colore)-->  notification area
```

- Il plugin ascolta gli eventi della sessione (`session.status`, `session.idle`,
  `message.updated`, `message.part.updated`, `permission.updated`, `permission.replied`,
  `session.error`, `session.created/deleted`) e i hook `tool.execute.*` / `permission.ask`.
  Tiene uno stato per sessione e invia quello più "urgente".
- Il plugin manda una riga per **sessione** (`sessions`): id, progetto, cartella, stato,
  attività (tool in corso), costo 24h/7d e i token per modello. Il tray raggruppa per
  progetto e calcola i totali come differenza rispetto all'ultimo valore ricevuto: se
  opencode riparte, il totale non torna mai indietro.
- **Il giallo è una richiesta aperta, non un "ho finito"**: resta acceso finché la domanda, la
  permissione o l'errore non è stato soddisfatto. Se opencode ha semplicemente finito di scrivere
  e aspetta il prossimo messaggio, l'icona resta **nera**; il giallo si accende solo quando
  serve davvero una tua azione.
- Ogni 4 secondi il plugin manda un heartbeat: serve a far sapere all'app tray che opencode è
  vivo. Se i heartbeat spariscono (opencode chiuso) per `heartbeatTimeoutSec`, l'icona resta nera.
- L'app tray è un server TCP minimo su `127.0.0.1` (nessun `HttpListener`, quindi nessun
  URL ACL e nessun admin) e un'istanza singola (mutex).
- Se ci sono più opencode aperti, l'icona mostra lo stato peggiore e il menu mostra quante
  istanze sono collegate.

### Struttura del progetto

```
silentnotification_opencode/
├── Install.cmd / Uninstall.cmd     wrapper comodi
├── install.ps1                     installer
├── uninstall.ps1                   disinstallatore (copiato anche nella cartella di installazione)
├── build.ps1                       compilazione (csc.exe del .NET Framework)
├── install-linux.sh                 installer Linux (GNOME)
├── linux-start.sh                   avvio manuale listener + estensione
├── plugin/opencode-tray.js         plugin opencode (copiato in ~/.config/opencode/plugins/; invariato su Linux)
├── src/OpencodeTray.cs             app tray Windows: icona, stati, menu, server
├── gnome-extension/                estensione GNOME Shell + listener Python + cost-info-window
├── dist/                           eseguibile compilato (build.ps1)
└── readme.md                       questo file
```

Log: `%APPDATA%\opencode-tray\tray.log` (cambiamenti di stato e connessioni/disconnessioni).

### File di stato (perché i costi non si perdono)

| File | Contenuto | Potatura |
| --- | --- | --- |
| `%APPDATA%\opencode-tray\plugin-state.txt` | sessioni opencode, token e costi per modello, cesti orari, step già contati | sessioni > 45 gg, cesti > 31 gg, step > 14 gg |
| `%APPDATA%\opencode-tray\cost-base.txt` | basi dell'azzeramento "Azzera conteggio costi" | manuale (si riscrive a ogni azzeramento) |

Il plugin salva `plugin-state.txt` ogni 20 secondi (in scrittura atomica) e lo rilegge
all'avvio: **riavviando opencode il totale NON riparte da zero** e gli step già contati non
vengono contati due volte. L'app tray salva le basi di conteggio: riavviandola, il
conteggio riparte da dove era stato azzerato.

## Diagnostica

```powershell
# processo e porta
Get-Process OpencodeTray
Get-NetTCPConnection -LocalPort 47110 -State Listen

# stato corrente
Get-Content $env:APPDATA\opencode-tray\tray.log -Tail 20

# test delle icone senza opencode (attenzione per 15 secondi)
& "$env:LOCALAPPDATA\Programs\opencode-tray\OpencodeTray.exe" --test-attention
```

Se la porta è occupata da un'altra istanza l'app lo segnala nel log e nel tooltip
(`porta 47110 occupata`): per cambiarla, modifica `port` in `settings.json` **e** la variabile
`OPENCODE_TRAY_PORT` dell'ambiente in cui gira opencode.

## Note / problemi noti

- **Il plugin va ricaricato**: dopo ogni modifica a `opencode-tray.js` riavvia opencode.
- **Antivirus**: alcuni prodotti (es. Sophos Intercept X) bloccano l'eseguibile appena
  compilato. Per questo `build.ps1` non usa `/optimize+` e verifica che il file sia
  leggibile; se resta bloccato, aggiungi la cartella di installazione tra le esclusioni.
- **Popup di Windows**: se in futuro attivi il balloon e non compare, controlla
  *Impostazioni → Sistema → Notifiche*.
- **Più istanze**: l'app tray ne ammette una sola (mutex `Local\OpencodeTray.SingleInstance`).

## Diagnostica rapida

```powershell
# aprire la finestra della stima costi senza passare dal menu
& "$env:LOCALAPPDATA\Programs\opencode-tray\OpencodeTray.exe" --show-costs

# aprire il menu dell'icona
& "$env:LOCALAPPDATA\Programs\opencode-tray\OpencodeTray.exe" --show-menu
```

Fatto: menu senza le voci di prova, stima costi riformattata con proiezione
settimanale, costi attribuiti per sessione/progetto, elenco dei progetti nel menu
e token openrouter esclusi dalla stima.

