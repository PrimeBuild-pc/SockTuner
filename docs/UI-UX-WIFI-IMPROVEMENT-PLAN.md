# Piano UX/UI e diagnostica Wi‑Fi

**Stato:** fasi 0–4 implementate; verifiche automatiche completate. Le verifiche hardware, DPI, Narrator e High Contrast restano manuali.
**Ambito verificato:** WPF su .NET 10, senza migrazione a WinUI 3 e senza nuove dipendenze UI.
**Principio guida:** prima fatti misurati, poi diagnosi, infine azioni esplicite e reversibili.

## Decisioni ferme

- WPF e i controlli nativi restano la base dell'interfaccia.
- Le Windows Design Guidelines sono il riferimento principale; le raccolte `awesome` restano soltanto indici di lettura.
- La diagnostica Wi‑Fi principale usa le API native Windows ed è passiva: nessuna deauthentication, injection, Evil Twin, WPS attack, cracking o scansione offensiva.
- `wifit3` non entra nel percorso diagnostico principale. Potrà essere rivalutato soltanto come launcher esterno indipendente, senza bundling né parsing del suo output attuale.
- Non viene introdotto un framework MVVM o un toolkit grafico per correggere problemi risolvibili con WPF, risorse XAML e piccoli componenti locali.

---

## 1. Audit UI/UX

### Punti già solidi

- Palette e stili sono centralizzati in `App.xaml`; colori semantici distinguono stato buono, avviso e pericolo.
- La struttura generale segue un flusso sensato: **Overview → Inventory → Measure → Act → Records**.
- I controlli principali sono WPF nativi; salvataggi e aperture usano i dialog standard di Windows.
- Le operazioni che modificano il sistema dichiarano rischio, consenso, elevazione, anteprima, verifica e rollback.
- Tabelle tecniche, copia ed export sono appropriati al tipo di prodotto.
- Sono presenti shortcut utili (`F5`, `Ctrl+F`, `Ctrl+K`, `Ctrl+1…9`), focus da tastiera, alcuni nomi Automation e una live region per il monitor continuo.
- La finestra ripristina la geometria solo se rimane raggiungibile su un monitor esistente.
- Non sono presenti animazioni decorative o dipendenze visive superflue.

### Problemi e priorità

| Priorità | Area | Evidenza | Impatto | Correzione proposta |
|---|---|---|---|---|
| P0 | Layout adattivo | Header, form diagnostici e righe comando usano molte colonne a larghezza fissa; la finestra minima è 1040×680 e la navigazione occupa 210 px fissi. | Controlli compressi o tagliati con DPI elevato, testi russi/cinesi o finestra minima. | Spezzare i form densi in due righe o `WrapPanel`; usare colonne elastiche con minimi solo dove necessari; verificare 100/150/200% DPI. |
| P0 | High Contrast e tema sistema | Palette scura e colori hard-coded; title bar forzata scura; template custom non hanno fallback High Contrast. | Contrasto e stati possono diventare illeggibili per utenti con esigenze visive. | Conservare il tema scuro, ma aggiungere override High Contrast basati su brush di sistema e non forzare la title bar quando High Contrast è attivo. |
| P0 | Stati e feedback | Errori, attese, vuoti e successi sono distribuiti tra status bar e `TextBlock`; solo pochi stati sono live region. | Il risultato di un'azione può essere lontano dal controllo e non annunciato da screen reader. | Un unico pattern locale `Loading / Empty / Result / Error`, testo vicino all'azione e annuncio Automation per operazioni asincrone. |
| P1 | Navigazione | 20 destinazioni selezionabili in una sola barra; Wi‑Fi è nascosto in fondo a “Recommendations”; `Ctrl+1…9` seleziona le prime nove destinazioni, non il primo tab di ogni gruppo come indica il commento. | Scopribilità bassa e modello mentale incoerente. | Mantenere i gruppi, aggiungere **Wi‑Fi diagnostics** in Measure, correggere descrizione/semantica shortcut e non aggiungere altre destinazioni senza consolidare. |
| P1 | Gerarchia delle pagine | Titoli pagina da 22 px, titoli card da 18/20 px e label uppercase da 11 px sono applicati in modo non uniforme; alcune pagine iniziano con un header, altre direttamente con una card-form. | Scansione visiva irregolare e aspetto meno professionale. | Introdurre stili nominati per page title, section title, card title, body, caption e field label; usare una struttura pagina comune. |
| P1 | Accessibilità dei form | Molti campi hanno `AutomationProperties.Name`, ma le label visuali sono `TextBlock` non associate; focus ed errori non sono descritti sistematicamente. | Esperienza screen reader e tastiera parziale. | Conservare i nomi Automation, aggiungere `LabeledBy` dove utile, messaggi di validazione locali e focus esplicito sul primo campo non valido. |
| P1 | Azioni e sicurezza UI | “Delete selected” elimina la cronologia senza conferma; “Copy row” resta globale anche senza riga selezionata; alcune pagine mostrano più azioni con peso visivo simile. | Errori evitabili e priorità poco chiara. | Conferma per eliminazione irreversibile, disabilitazione contestuale, una primary action per task e azioni secondarie raggruppate. |
| P1 | Tabelle | Le griglie sono efficaci ma molto larghe; spesso il dettaglio è affidato a tooltip e scrollbar orizzontale. | Lettura lenta, soprattutto con tastiera, zoom o traduzioni lunghe. | Mantenere le colonne essenziali visibili e spostare identificatori/raw fields in un pannello dettaglio o nell'export. |
| P2 | Persistenza del contesto | Geometria finestra è salvata, ma non tab, filtri o radio selezionata. | Ritorno al lavoro meno rapido. | Persistire soltanto la sezione attiva e l'interfaccia Wi‑Fi selezionata; non salvare ogni stato UI. |
| P2 | Localizzazione | La traduzione a runtime è pragmatica, ma diverse stringhe dinamiche Wi‑Fi e IRQ restano costruite direttamente in inglese. | Interfaccia mista nelle lingue non inglesi. | Portare le nuove stringhe attraverso `Loc.T/Loc.F` e correggere le stringhe dinamiche toccate dal refactoring, senza riscrivere l'intero sistema i18n. |

### Valutazione complessiva

La UI è funzionale e già più prudente di molti strumenti di tuning, ma è cresciuta per aggiunte successive dentro `MainWindow.xaml` e `MainWindow.xaml.cs`. Il problema principale non è lo stile visivo: è la densità, la variabilità dei pattern e la scarsa visibilità della diagnostica Wi‑Fi. La correzione deve essere incrementale, non una riscrittura.

---

## 2. Design rules condivise

### Struttura

1. Ogni destinazione usa: **titolo + descrizione breve → comandi del task → stato locale → contenuto**.
2. Una card raggruppa un task o un risultato, non ogni singolo valore.
3. Una sola primary action visibile per task; cancel, export e reset restano secondarie.
4. Le pagine lunghe usano disclosure progressiva: sintesi prima, evidenza e raw data dopo.
5. Le tabelle mostrano subito le colonne necessarie alla decisione; ID, raw fields e dettagli completi restano nel pannello dettaglio/copia/export.

### Token minimi

Da definire come risorse XAML, riusando i valori esistenti dove già corretti:

- spacing: 4, 8, 12, 16, 24, 32 px;
- page padding: 24 px; distanza tra sezioni: 24 px; card padding: 16 px;
- control height desktop: minimo 32 px;
- page title: 24 px semibold;
- section title: 20 px semibold;
- card title: 16–18 px semibold;
- body: 13 px; caption/field label: 11–12 px;
- bordi: 1 px; focus: 2 px senza cambiare le dimensioni del controllo.

### Colore e stato

- Colore mai come unico indicatore: mantenere testo/simbolo (“Good”, “Warning”, “High”).
- Blu solo per selezione, link e azione primaria; giallo per cautela; rosso per errore/pericolo; verde per esito confermato.
- Stati richiesti per ogni controllo: normal, hover, pressed, keyboard focus, disabled e validation error.
- High Contrast sostituisce i brush applicativi con brush di sistema; nessuna informazione deve dipendere da sfondi custom.

### Contenuto e tono

- Frasi brevi, tecniche e verificabili.
- Distinguere sempre **misurato**, **dedotto**, **non disponibile** e **azione suggerita**.
- Evitare “optimized”, “best” o punteggi sintetici non dimostrabili.
- Un errore API mostra cosa non è disponibile, perché se noto e quale azione può sbloccarlo.

### Accessibilità e tastiera

- Ordine Tab uguale all'ordine visivo.
- Tutti i campi hanno nome accessibile; label visuali associate con `AutomationProperties.LabeledBy` quando pratico.
- Operazioni asincrone annunciano avvio, completamento, annullamento ed errore.
- `Enter` attiva la primary action solo nei dialog o nei task senza rischio; `Esc` annulla dove sicuro.
- Focus riportato al titolo/stato della nuova sezione dopo navigazione programmatica.

### Responsive WPF

- Target minimo: 1040×680 a 100%, ma layout utilizzabile anche a 150% DPI.
- Le righe con più di quattro input si spezzano; i pulsanti non devono comprimere i campi sotto il minimo leggibile.
- Le DataGrid possono scorrere orizzontalmente, ma la decisione principale non deve richiederlo.
- Nessun nuovo controllo custom se `Grid`, `WrapPanel`, `ItemsControl`, `Expander` o `DataGrid` coprono il caso.

---

## 3. Inventario schermate

| Gruppo | Schermata | Scopo attuale | Nota audit |
|---|---|---|---|
| Overview | Dashboard | Stato sistema, health check, baseline e recovery | Buona sintesi; cinque KPI rigidi da rendere adattivi. |
| Inventory | Adapters | Identità, link, indirizzi, driver | Troppe colonne primarie; utile pannello dettaglio. |
| Inventory | NDIS & drivers | Proprietà avanzate driver | Appropriata per utenti tecnici; raw keyword va preservata. |
| Inventory | Routes & DNS | Route e metriche interfaccia | Due dataset correttamente separati, ma molto densi. |
| Inventory | Network profiles | Profili NLM | IDs possono passare a dettaglio/export. |
| Inventory | Network bindings | Protocol/filter bindings | Tabella più larga della finestra; priorità alle colonne human-readable. |
| Inventory | Offloads | Stato globale e per adapter | Buona separazione; manca un empty/error state uniforme. |
| Inventory | TCP settings | Template ed effective policy | Raw state utile, ma da presentare dopo la sintesi. |
| Inventory | QoS policies | Inventario e creazione policy DSCP | Mescola inventory e action; mantenere ma separare visivamente i due task. |
| Inventory | Winsock catalog | Provider nativi | Destinazione specialistica corretta, non da promuovere. |
| Measure | Gaming diagnostics | Latenza, jitter, loss, path, playability, monitor | È la pagina più importante e più sovraccarica; richiede sintesi iniziale e dettagli progressivi. |
| Measure | Throughput & bufferbloat | Throughput e latenza sotto carico | Buona separazione dalle probe leggere; form iniziale troppo largo. |
| Measure | DNS resolvers | Benchmark e applicazione resolver | Lettura e modifica sono ben dichiarate; auto-apply richiede forte chiarezza di stato. |
| Act | Interfaces | Consiglio e coda modifiche adapter | Buon gating; azioni contestuali da disabilitare/descrivere in modo uniforme. |
| Act | Recommendations | Azioni locali/router e radio Wi‑Fi | Il solo output Wi‑Fi è nascosto qui e ridotto a testo libero. |
| Act | Interrupt affinity | Inventario, policy e applicazione IRQ | Area avanzata coerente, ma richiede warning e focus error robusti. |
| Act | Tuning plan | Preview, apply, drift e rollback | Flusso di sicurezza solido; densità elevata ma giustificata. |
| Records | Tools & references | Link esterni verificati | Luogo corretto per un eventuale launcher esterno futuro. |
| Records | History & comparison | Confronto, trend, drift, export e delete | “Delete” necessita conferma; azioni da rendere contestuali alla selezione. |
| Records | Preferences | Lingua, retention, report compatibilità | “Help improve” non è una preference: può restare temporaneamente, ma va distinta come contributo. |
| Dialog | Compatibility report preview | Revisione JSON prima del salvataggio | Buon uso di owner, default/cancel e testo monospaziato. |

**Nuova destinazione proposta:** `Measure → Wi‑Fi diagnostics`. Non si propongono altre nuove schermate.

---

## 4. Inventario diagnostica Wi‑Fi esistente

### Raccolta nativa già disponibile

`WindowsWifiInventory` usa direttamente WLAN API e non invoca `WlanScan`:

- enumerazione interfacce WLAN;
- connessione corrente: SSID, BSSID, signal quality, TX/RX negotiated rate;
- BSS cached: SSID, BSSID, RSSI, PHY raw, frequenza e information elements;
- derivazione banda e canale per 2.4/5/6 GHz;
- parsing HT/VHT per larghezza 20/40/80/160 MHz e span occupato;
- elenco reti vicine e overlap spettrale.

### Analisi già disponibile

`WifiRadioAnalyzer` produce finding puri e testabili per:

- segnale marginale o debole;
- rete equivalente su 5/6 GHz mentre il client usa 2.4 GHz;
- 40 MHz su 2.4 GHz;
- pressione co-channel e partial-overlap sopra una soglia RSSI;
- raccomandazione del canale 1/6/11 meno carico, pesata per RSSI.

La radio contribuisce anche a:

- localizzazione del bottleneck LAN/radio;
- router guidance;
- raccomandazioni derivate da diagnosis e bufferbloat;
- test unitari di ABI, banda/canale, overlap e finding.

### Limiti attuali della presentazione

- La raccolta avviene soltanto quando si costruiscono le Recommendations, non come parte visibile del percorso Measure.
- L'interfaccia mostra soltanto `inventory.Radios[0]`.
- I risultati sono concatenati in un unico `TextBlock`; non esistono stato per-interfaccia, tabella vicini o confronto temporale.
- Un errore WLAN è testo generico; accesso negato/location consent e servizio WLAN fermo non hanno percorsi distinti.
- SSID/BSSID entrano nel testo senza una policy UI/export specifica di redazione.

---

## 5. Lacune native

Ordine consigliato, dal necessario al miglioramento:

1. **Permesso location e codici errore.** Trattare esplicitamente `ERROR_ACCESS_DENIED` per le WLAN API soggette alle regole privacy di Windows; mostrare istruzione e link alle impostazioni, senza chiedere elevazione impropria.
2. **Più interfacce.** Conservare e mostrare ogni radio, stato di associazione ed errore per interfaccia; nessuna scelta implicita del primo elemento.
3. **Connection metadata.** Esporre PHY corrente, connection mode, authentication e cipher; distinguere i dati della connessione dai capability dell'adapter.
4. **Interface capability.** Leggere le capability native disponibili (PHY supportati e stato radio quando documentato) per spiegare “non supportato” versus “non usato”.
5. **IE parser completo e difensivo.** Aggiungere parsing testato di RSN, PMF, BSS Load/channel utilization quando pubblicato, HE Operation per 6 GHz e, solo se documentazione/driver lo rendono affidabile, EHT Operation. WPS è informativo, non una raccomandazione di performance.
6. **Larghezza 6 GHz.** Eliminare il fallback errato a 20 MHz quando l'AP pubblica soltanto HE Operation.
7. **Freshness.** Esporre timestamp/età del dato quando affidabile; dichiarare “cached by Windows”. Non presentare il numero di BSS come una scansione live.
8. **Osservazione temporale.** Campionare passivamente connessione corrente, RSSI, negotiated rate e cambi BSSID durante una finestra breve; il BSS cache resta secondario e non viene forzato.
9. **Correlazione locale.** Collegare radio evidence, gateway RTT/loss e counter delta dell'adapter; non attribuire al Wi‑Fi un problema che inizia dopo il gateway.
10. **Privacy/export.** Redigere BSSID, SSID/profile e interface GUID negli export support; documentare cosa resta nei report completi.

### Limiti che restano anche dopo il lavoro nativo

- WLAN API non fornisce un monitor-mode affidabile, retry frame completi, rumore/SNR universale o airtime reale di tutti i client.
- Il BSS list è una cache Windows e può essere incompleto o vecchio.
- RSSI e negotiated link rate non equivalgono a throughput o latenza.
- Un conteggio di reti vicine misura **pressione radio potenziale**, non utilizzo effettivo, salvo BSS Load pubblicato dall'AP.
- Legalità canali, DFS e potenza dipendono da paese, driver e AP; SockTuner non deve imporre canali 5/6 GHz.

---

## 6. Proposta: Gaming Wi‑Fi Diagnostic Engine

### Obiettivo

Produrre un report passivo, spiegabile e orientato al gaming che risponda a tre domande:

1. La connessione radio mostra un rischio locale misurabile?
2. Il comportamento del gateway conferma che la degradazione inizia prima di Internet?
3. Qual è la prossima azione verificabile, sul PC o sul router?

### Input

- snapshot WLAN per tutte le interfacce;
- snapshot adapter/counter già raccolti da SockTuner;
- opzionalmente report Gaming diagnostics e monitor continuo;
- opzionalmente serie passiva breve della connessione corrente.

### Pipeline minima

```text
WindowsWifiInventory
        ↓ facts
GamingWifiDiagnosticEngine (pure)
        ├─ connection assessment
        ├─ signal/rate stability
        ├─ channel pressure
        ├─ security/capability compatibility
        └─ correlation with gateway + adapter counters
        ↓
WifiDiagnosticReport
        ├─ verdict
        ├─ evidence[]
        ├─ findings[]
        ├─ unknowns[]
        └─ suggested next checks[]
```

Non serve un provider system o un plugin framework: un collector Windows e un analyzer puro coprono l'ambito reale.

### Output

- `Healthy`, `At risk`, `Degraded`, `Not enough data`, mai un punteggio percentuale arbitrario;
- severità, confidence, segmento e owner coerenti con i finding esistenti;
- evidenza numerica con unità e provenienza;
- unknown espliciti quando API, permesso o AP non espongono il dato;
- azione e verifica successiva per ogni finding.

### Regole diagnostiche iniziali

- RSSI debole/marginale resta un rischio radio, con isteresi se osservato nel tempo.
- Oscillazione RSSI/rate e cambi BSSID durante gateway spikes aumentano la confidence del finding LAN.
- Gateway stabile + game endpoint instabile non è classificato come Wi‑Fi.
- Errori/discard adapter crescenti durante perdita al gateway rafforzano il finding locale.
- 2.4 GHz con valida alternativa 5/6 GHz è una scelta suggerita, non una modifica automatica.
- 40 MHz su 2.4 GHz e partial overlap restano router findings.
- Channel utilization da BSS Load, se presente e fresco, vale più del semplice conteggio vicini.
- Authentication/cipher/WPS/PMF sono capability/security facts; non diventano finding di latenza senza evidenza di compatibilità o riconnessioni.

### Integrazione

- La pagina Wi‑Fi può produrre un report autonomo passivo.
- Gaming diagnostics acquisisce snapshot WLAN prima/dopo e passa l'evidenza all'engine.
- Recommendations riceve finding strutturati, non testo preformattato.
- History salva soltanto i campi necessari al confronto; support export redige gli identificatori.

---

## 7. Proposta UI Wi‑Fi

### Posizione

Nuovo tab **Wi‑Fi diagnostics** nel gruppo **Measure**, subito dopo Gaming diagnostics. Il riquadro Wi‑Fi nelle Recommendations diventa un riepilogo/link alla pagina, evitando due implementazioni.

### Layout

1. **Page header**
   - titolo e descrizione “Passive Windows WLAN data; no scan is triggered”;
   - selettore interfaccia visibile solo con più radio;
   - azione primaria `Refresh passive data`;
   - azione secondaria `Observe stability for 60 s`.

2. **Connection summary**
   - stato, SSID redatto opzionalmente, banda/canale/larghezza;
   - RSSI e quality;
   - TX/RX negotiated rate;
   - PHY, authentication/cipher e PMF quando noti;
   - età/fonte del dato.

3. **Gaming impact**
   - verdict testuale;
   - relazione con gateway: confirmed local / not correlated / not measured;
   - 3–5 evidenze principali, ordinate per impatto.

4. **Findings**
   - DataGrid accessibile: severity, finding, evidence, owner, action;
   - link contestuale a Gaming diagnostics o Recommendations.

5. **Nearby channel pressure**
   - tabella nativa per canale/banda con BSS count, strongest RSSI, overlap e utilization se disponibile;
   - eventuali barre realizzate con `ItemsControl`/`Grid`, sempre duplicate da valori testuali; nessuna chart library.

6. **Technical details**
   - `Expander` con BSSID, capability e IE-derived facts;
   - nascosto dalla vista decisionale ma copiabile.

7. **Permission/error state**
   - card dedicata per Location access, WLAN AutoConfig fermo, API non supportata o radio assente;
   - azione Windows pertinente, non un errore generico.

### Stati iniziali

- Wired/no radio: “No active Wi‑Fi connection”; non è un errore.
- Radio presente ma non associata: capability visibili, connection assessment non disponibile.
- Access denied: istruzione Location privacy.
- Cached data vecchio/ignoto: badge “Cached by Windows; age unknown”.
- Observe in corso: countdown, cancel e live status; nessuna scansione attiva.

---

## 8. Modifiche ordinate per priorità

### Fase 0 — baseline UI, nessuna feature

1. Aggiungere stili tipografici/spacing e pattern di stato in `App.xaml`.
2. Rendere adattivi header e form più densi, iniziando da Gaming diagnostics, Throughput e DNS.
3. Aggiungere override High Contrast e verificare focus.
4. Uniformare empty/loading/error e disabilitazione azioni contestuali.
5. Confermare la cancellazione della history.

**Gate:** build, test e verifica manuale a DPI/lingue diverse prima di toccare la diagnostica.

### Fase 1 — base WLAN nativa

1. Error mapping incluso access denied/location.
2. Multi-radio e connection metadata.
3. Parser IE puro e testabile: RSN/PMF, BSS Load, HE; EHT solo con casi documentati.
4. Freshness e privacy model.

### Fase 2 — engine

1. Introdurre `WifiDiagnosticReport` e analyzer puro.
2. Portare le regole esistenti senza cambiarne il significato.
3. Aggiungere correlazione con gateway e counter.
4. Aggiungere osservazione temporale passiva con limite fisso e cancel.

### Fase 3 — UI e integrazione

1. Aggiungere `Wi‑Fi diagnostics` come view WPF isolata.
2. Collegare findings strutturati a Recommendations.
3. Collegare snapshot Wi‑Fi al run Gaming diagnostics.
4. Aggiungere history/export con redazione.
5. Correggere le stringhe localizzate toccate.

### Fase 4 — rifinitura

1. Test tastiera/screen reader/High Contrast.
2. Test live su Windows 10/11, radio singola/multipla, Wi‑Fi 5/6/6E/7 dove disponibile.
3. Aggiornare architettura e ADR.
4. Solo a questo punto rivalutare un launcher esterno wifit3.

---

## 9. File previsti

La lista è intenzionale, non un impegno a creare tutti i file se il lavoro può restare più piccolo.

### Da modificare

- `src/SockTuner/App.xaml` — token, stili di stato, High Contrast e focus.
- `src/SockTuner/MainWindow.xaml` — layout adattivo, nuovo tab e link/riepiloghi.
- `src/SockTuner/MainWindow.xaml.cs` — sola orchestrazione tra snapshot, engine, history e view.
- `src/SockTuner/Models/WifiRadio.cs` — facts nativi e multi-radio.
- `src/SockTuner/Services/Collection/WindowsWifiInventory.cs` — API/error mapping/IE parsing.
- `src/SockTuner/Services/Diagnosis/WifiRadioAnalyzer.cs` — regole esistenti migrate nel report strutturato o riusate dall'engine.
- `tests/SockTuner.Tests/WifiRadioAnalyzerTests.cs` — parser e regressioni delle regole correnti.
- `src/SockTuner/Assets/i18n/translations.json` — nuove stringhe e stringhe dinamiche toccate.
- `docs/ARCHITECTURE.md` — flusso WLAN nativo e confini passivi.

### Nuovi solo se confermati dall'implementazione

- `src/SockTuner/Models/WifiDiagnostics.cs` — report, sample e stati; un solo file di modelli coesi.
- `src/SockTuner/Services/Diagnosis/GamingWifiDiagnosticEngine.cs` — analyzer puro.
- `src/SockTuner/Views/WifiDiagnosticsView.xaml`
- `src/SockTuner/Views/WifiDiagnosticsView.xaml.cs`
- `tests/SockTuner.Tests/GamingWifiDiagnosticEngineTests.cs`
- `docs/adr/0001-native-passive-wifi-diagnostics.md`

Non è previsto un progetto, provider, plugin framework o pacchetto NuGet aggiuntivo.

---

## 10. Rischi

| Rischio | Mitigazione |
|---|---|
| ABI WLAN errata o struct diversa per architettura | Struct size test, fixture binarie del parser e test live gated. |
| Location privacy restituisce access denied | Stato distinto e istruzione Windows; nessun fallback a processi esterni. |
| Cache BSS obsoleta produce falsa “congestione” | Etichetta freshness, termine “channel pressure”, confidence ridotta senza BSS Load. |
| HE/EHT parsing incompleto | Parser bounds-checked, unknown invece di guess, fixture documentate. |
| Polling incide su radio/UI | Intervallo moderato, durata limitata, lavoro off UI thread e cancel. |
| Correlazioni scambiate per causalità | Evidence + confidence + segmento; gateway comparison obbligatoria per conferma locale. |
| SSID/BSSID nei report | Export support redatto per default e warning sul report completo. |
| Regressioni layout con localizzazione/DPI | Matrix manuale e layout che va a capo, non larghezze aumentate. |
| High Contrast rotto dai template custom | Override con brush di sistema e test manuale Windows. |
| MainWindow continua a crescere | Isolare soltanto la nuova pagina Wi‑Fi; nessuna riscrittura MVVM dell'app. |
| Hardware/driver non espone dati moderni | Ogni campo nullable con stato “Not reported”; nessuna feature gate globale. |
| Integrazione wifit3 introduce GPL, firmware, UAC o attività attive | Nessun bundling/import; eventuale launcher indipendente solo dopo ADR e verifica RX-only. |

---

## 11. Test

### Automatici

- banda/canale/frequenza e span 20/40/80/160/6 GHz;
- parser TLV troncati, unknown e duplicati senza eccezioni;
- RSN/PMF, BSS Load, HE ed eventuale EHT con fixture minime;
- error mapping per service stopped, access denied e generic Windows error;
- multi-radio, radio non associata e dati parziali;
- regole engine: weak RSSI, oscillazione, roam, overlap, alternative band;
- correlazione: gateway degradato vs gateway stabile;
- nessun finding di performance basato soltanto su security metadata;
- redazione SSID/BSSID/GUID negli export support;
- regressioni delle regole `WifiRadioAnalyzer` esistenti;
- test ABI esistenti e `dotnet test` completo.

### Live Windows, esplicitamente opt-in

- Windows 10/11 con WLAN AutoConfig attivo e fermo;
- Location consentito e negato;
- wired-only, radio non associata, una e più radio;
- rete 2.4/5/6 GHz quando disponibile;
- verifica che refresh/observe non chiami `WlanScan` e non disconnetta la radio;
- cancel dell'osservazione e chiusura finestra durante il sampling.

### UI manuali

- 1040×680 e 1320×820 a 100%, 150% e 200% DPI;
- inglese, spagnolo, russo e cinese semplificato;
- solo tastiera: navigazione, refresh, observe, cancel, tabelle e focus error;
- Narrator: nomi, ordine, live status e finding;
- High Contrast;
- empty/loading/error/partial/success per ogni blocco;
- resize rapido e display multipli;
- conferme per delete/apply/rollback ed export sensibili.

Non viene aggiunto un framework di visual regression finché i test manuali e WPF esistenti coprono il bisogno.

---

## 12. Fuori scope

- Migrazione a WinUI 3, MAUI, Avalonia o web UI.
- Adozione di FluentWPF, ModernWpf, MaterialDesign, chart library o framework MVVM.
- Redesign totale o riscrittura di `MainWindow`.
- Monitor mode, packet injection, deauthentication, Evil Twin, WPS brute force/Pixie Dust, cracking o capture offensivo.
- Controllo automatico di canale, potenza, roaming, router o access point.
- Promesse di “zero lag”, punteggi Wi‑Fi sintetici o tuning non supportato da misure.
- Bundling, fork o import del codice Python di wifit3.
- Parsing di `.pcap`/`.hc22000` come inventario Wi‑Fi.
- Download automatico di tool o driver esterni.
- Redistribuzione di firmware, WinUSB helper o binari unsigned.

### Condizione minima per rivalutare wifit3

Un futuro launcher resta opzionale e separato. Un'integrazione dati richiederebbe almeno una CLI documentata e stabile simile a:

```text
wifit3 --passive-scan --duration 15 --json result.json
wifit3 --list-devices --json
```

con modalità RX-only verificabile, nessun `pbc_enabled` implicito, schema versionato, exit code definiti e revisione separata di licenza/supply-chain. Fino ad allora, l'alternativa nativa Windows è il percorso raccomandato.

---

## Gate di avvio

Questo documento chiude la fase di audit. Il primo changeset applicativo deve essere **Fase 0** e non deve includere contemporaneamente la nuova diagnostica Wi‑Fi: layout/accessibilità e feature devono restare revisionabili separatamente.
