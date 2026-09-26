# Archivio delle conclusioni del corpus `research/`

> Documento sostitutivo per il materiale di ricerca voluminoso rimosso dal PC. Conserva provenienza, inventario e decisioni utili a SockTuner; non rende autorevole né riutilizzabile il materiale originale.
>
> Analisi: **solo statica**, conclusa il **10 settembre 2026**. Nessuno script o binario del corpus è stato eseguito.

## 1. Regole di interpretazione

- Script, preset, database e report di terzi erano fonti di idee, non specifiche tecniche.
- Un valore leggibile o scrivibile non è automaticamente supportato, efficace o vantaggioso.
- Una stringa trovata in un binario prova meno di un consumer confermato; un consumer confermato prova meno di un effetto misurato.
- L’assenza da una scansione statica o da una breve traccia runtime non prova che un valore sia placebo.
- Ogni mutazione SockTuner resta subordinata a capacità live, snapshot tipizzato, validazione, read-back, verifica dell’effetto e rollback esatto.
- Nessun codice privo di licenza esplicita viene copiato o redistribuito.

## 2. Inventario congelato prima della pulizia

Dimensioni e conteggi si riferiscono a `C:\Users\Lorenzo\Documents\Projects\SockTuner\research` prima della rimozione dei programmi.

| Area | File | Byte | Contenuto | Disposizione |
| --- | ---: | ---: | --- | --- |
| `links/` | 3 | 2.673 | URL, shortcut e riferimento Win Toolkit | conservato |
| `notes/` | 4 | 14.001 | teoria, comandi NDIS, idea route optimizer, nota remota non sicura | conservato |
| `projects/` | 170 | 159.914.579 | GameNetAnalyzer e studi/script bufferbloat | spostato nel Cestino il 10 settembre 2026 |
| `results/` | 4 | 808.258 | CSV, JSON e screenshot di test personali | conservato |
| `scripts/` | 22 | 1.473.895 | diagnostica e tuning PowerShell/batch | spostato nel Cestino il 10 settembre 2026 |
| `tools/` | 6.126 | 3.140.221.321 | utility, binari, screenshot e cinque generazioni Zenit | spostato nel Cestino il 10 settembre 2026 |
| **Totale** | **6.330** | **3.302.436.244** | escluso `research/README.md` | — |

Spazio spostato nel Cestino: **3.301.609.795 byte** (circa **3,07 GiB**) da `projects/`, `scripts/` e `tools/`.

### 2.1 Script

`diagnostics/` conteneva:

- `FIND-NIC-CLASS-PATH.bat`;
- `Get Adapter_GUID&PATH.ps1`;
- `NetworkDiagnostics.ps1`;
- `gaming_net_diagnostic.ps1`;
- `sniff_game_server.ps1`.

`tuning/` conteneva:

- `Auto MTU.bat`;
- `Command Lines BBR_FIX_W11.bat`;
- `DEVICE-TWEAKER-UPDATE-2026-LLG-X-LLC-UPDATED.ps1`;
- `DISABLE-POWERSAVING.bat`;
- due copie di `Disable Nagle Algorithm.ps1` e `Disable Nagle’s Algorithm.bat`;
- `Disable-NIC-PowerSavings.ps1`;
- `InterruptModerationLevel.ps1`;
- `Low_Latency.bat`;
- `Network Adapter Settings.bat`;
- `Network Test.bat`;
- `Network Tweaker 20230709.ps1`;
- `NetworkOptimizer.ps1`;
- `OPTIMIZE-NETWORKING.bat`;
- `Zenit_by_Jackpot_23.04.2026.ps1`;
- `disablenetworkpowermanagement.ps1`.

Conclusione conservata: i comandi di inventario sono utili come checklist, ma le raccomandazioni “sempre disabilita” e i valori fissi non sono evidenza. SockTuner usa API/CIM native e proprietà dichiarate dal driver, non esegue questi script.

### 2.2 Progetti

| Progetto | Provenienza congelata | Licenza osservata | Conclusione conservata |
| --- | --- | --- | --- |
| `GameNetAnalyzer` | `https://github.com/PrimeBuild-pc/GameNetAnalyzer`, commit `4a7919fef9d645c964f49c2ae36b3e9618dbe339` | MIT | metriche di sessione, jitter/burst/spike, confronto run ed endpoint di gioco sono utili; SockTuner evita la dipendenza tshark/Npcap e preferisce socket/ETW nativi |
| `Bufferbloat vs` | `https://github.com/PrimeBuild-pc/bufferbloat-analysis`, commit `1136e3aa7d9837fa03824d2e582fbcd0ceef2052` | nessun file di licenza osservato nella copia | confrontare latenza idle e sotto carico; percentili e direzioni separate; la correzione reale è di norma SQM/AQM sul router |
| `bufferbloat-bufferbloat` | origine non documentata, sette batch locali | non identificata | nessun codice da riusare; i batch applicano ricette, non una diagnosi affidabile |

La cattura GameNetAnalyzer includeva un PCAP personale di circa 110 MB e report derivati: erano esempi, non fixture pubblicabili.

### 2.3 Tool

| Cartella | File | Byte | Decisione |
| --- | ---: | ---: | --- |
| `FAST-screens` | 35 | 3.133.756 | conservati concettualmente baseline, confronto e guida; rifiutati percentile cloud, geolocalizzazione necessaria e geofence/firewall dei datacenter |
| `Registry_Truth_S.U.C.Ker` | 14 | 214.085.684 | utile l’idea di diff `.reg`/stato live e traccia runtime; database e verdict non sono attendibili né licenziati per l’import |
| `Script & tools` | 7 | 26.077.533 | GoInterruptPolicy resta un riferimento separato; gli altri script/preset non diventano catalogo |
| `Star Ethernet Analyzer` | 7 | 103.716.596 | utile importare serie CSV di RTT, jitter e loss; non copiare grade basato soltanto su `max RTT - min RTT` o medie aggregate |
| `WinMTR-v092` | 19 | 4.250.224 | WinMTR GPLv2 dimostra l’utilità di ping/traceroute ripetuti; SockTuner implementa la funzione nativamente e non include il binario |
| `Zenit` | 6.044 | 2.788.957.528 | cinque generazioni analizzate staticamente; conclusioni nei documenti Zenit e nel §5 |

`Registry_Truth_S.U.C.Ker` associava eventi Procmon ai soli nomi valore tramite substring e poteva chiamare “default usato” una semplice assenza. Il suo backup ometteva i valori originariamente assenti e quindi non consentiva rollback esatto. L’idea di osservazione runtime resta valida soltanto con matching del percorso completo e senza trasformare “non osservato” in un verdetto negativo.

### 2.4 Link, note e risultati rimasti

- `links/repo.txt`: repository PrimeBuild per GameNetAnalyzer e Network-Tweaker.
- `pingpackettest.com-game-pro-esports.url` e `Win Toolkit.lnk`: riferimenti esterni, non dipendenze.
- `notes/route-optimization-feature-idea.md`: un prodotto ExitLag/LagoFast richiede relay e transit propri; SockTuner può al massimo misurare percorsi già disponibili o un relay portato dall’utente, previa revisione separata.
- `notes/Fortnite Ping Tool.txt`: contiene `irm ... | iex`; pattern esplicitamente rifiutato.
- `notes/Teoria rete.txt`: contiene affermazioni universali e valori fissi non dimostrati; mantenuta solo come provenienza delle idee.
- `results/`: quattro risultati personali utili come esempi manuali, non come benchmark generalizzabili.

## 3. Conclusioni funzionali consolidate per SockTuner

### Da mantenere

1. Inventario per adapter con identità stabile, driver, link, proprietà avanzate e capacità live.
2. Misure separate di latenza base, jitter, perdita, stabilità nel tempo e latenza sotto carico.
3. Traceroute ripetuto e distinzione fra host, LAN/router, access link e routing/peering.
4. Confronti prima/dopo e fra sessioni, con contesto del carico e del tick rate.
5. Correlazione processo-endpoint tramite ETW o altra fonte nativa affidabile, non euristiche sul nome del processo o su endpoint UDP inesistenti.
6. Diagnosi bufferbloat con spiegazione che endpoint tuning non sostituisce SQM/CAKE/FQ-CoDel sul router.
7. Catalogo driver-advertised: valori enum/range del driver sono l’allowlist; proprietà vendor non documentate restano inventario o ricerca.
8. Piani trasparenti e atomici, mai pulsanti opachi “ottimizza tutto”.

### Da rifiutare

- disabilitare universalmente ECN, auto-tuning, timestamp, offload, flow control, RSS o interrupt moderation;
- derivare RSS soltanto dal numero di core o forzare queue count fuori capacità;
- scrivere valori NDIS non pubblicizzati o creare `Ndi\Params` per simulare supporto;
- applicare Nagle/ACK registry tweaks a tutte le interfacce come ottimizzazione gaming UDP;
- assumere MTU da ISP o da un probe fallito;
- disabilitare tutti i binding tranne IPv4/IPv6;
- usare reset Winsock/IP come rollback;
- installare driver, runtime o pacchetti tramite URL mutabili o pipeline `download-and-execute`;
- trasformare un read-back, una stringa binaria o una singola traccia in prova di beneficio;
- includere PCAP, report personali, database sconosciuti o binari del corpus nel prodotto.

## 4. Mappatura al progetto

Le decisioni già assorbite sono descritte principalmente in:

- [`ARCHITECTURE.md`](ARCHITECTURE.md): API native, confine privilegi, diagnostica e limiti di accuratezza;
- [`DELIVERY_PLAN.md`](DELIVERY_PLAN.md): integrazione del corpus, catalogo NDIS/TCP, misure e remediation;
- [`ROADMAP.md`](ROADMAP.md): stato dei workstream, evidence note, bufferbloat e funzioni rinviate;
- [`PRODUCT_SCOPE.md`](PRODUCT_SCOPE.md): esclusioni e promesse che il prodotto non può fare;
- serie [`JACKPOTS_ZENIT_REFERENCE.md`](JACKPOTS_ZENIT_REFERENCE.md), [`JACKPOTS_ZENIT_4.0_DELTA.md`](JACKPOTS_ZENIT_4.0_DELTA.md), [`JACKPOTS_ZENIT_5.0_DELTA.md`](JACKPOTS_ZENIT_5.0_DELTA.md), [`JACKPOTS_ZENIT_5.3_DELTA.md`](JACKPOTS_ZENIT_5.3_DELTA.md), [`JACKPOTS_ZENIT_7.0_LATENCY_SUITE_DELTA.md`](JACKPOTS_ZENIT_7.0_LATENCY_SUITE_DELTA.md) e [`JACKPOTS_ZENIT_NDIS_CANDIDATES.md`](JACKPOTS_ZENIT_NDIS_CANDIDATES.md).

L’unico gap concreto già emerso da Zenit 7.0 resta il controllo RSS nativo e tipizzato: prima completare l’inventario, poi eventualmente trattare l’intera tupla RSS come una transazione con verifica dell’indirection table. Nessun preset generico e nessun fallback registry.

## 5. Delta finale: Zenit Latency Suite / Engine v8.0

### 5.1 Provenienza e confronto

Percorso originale del corpus condiviso con ZapTweaks, successivamente spostato nel Cestino:

`C:\Users\Lorenzo\Downloads\Zenit Latency Suite`

La GUI dichiara Engine v8.0; launcher e manuali riportano ancora v7.0. Il corpus nuovo contiene **6.091 file / 3.840.378.607 byte**. Confronto SHA-256 completo con `research/tools/Zenit/Zenit - Jackpot Latency Suite`:

- copia precedente: 5.608 file / 2.231.427.631 byte;
- 5.578 file identici;
- 28 file modificati;
- 485 file aggiunti;
- 2 file rimossi;
- delta netto: 483 file e 1.608.950.976 byte.

Quasi tutto il peso aggiunto è costituito da database/progetti Ghidra e risultati derivati, non da nuove funzioni utente.

Fingerprint dei file principali analizzati:

| File | SHA-256 |
| --- | --- |
| `app.py` | `f522aead9899fe300bf70571b6f09e68ff983bd89ca2f6c1742b5af0400e6948` |
| `IMOD-Test.py` | `31ffb4bfede24ffa6a8b15260abb40b3343791c61883c21347d144b8c0dd9c4d` |
| `Nic.ps1` | `85521e65270f664ffcd32c34cdf8a0b942f1876883e5a6ef2673dd2325d82878` |
| `WinTweakVerifier/procmon_verifier.py` | `57b3dce27073645c04253e586c8f7b1a1abe3d87e825b6880b4566c6e1635b3a` |
| `WinTweakVerifier/scripts/GhidraAnalyzer.java` | `9831f0ad3aadee99a7598c60a225d9c7ef38810ebfbcde5fa8caf08a9206ee53` |

Il corpus applicativo Zenit non espone una licenza riutilizzabile identificata. Ghidra ha la propria licenza, ma la sua presenza non concede diritti sul codice Zenit, sui profili o sui database aggregati.

### 5.2 Novità utili come metodo

- Worker asincroni per operazioni di rete, restore point e merge Procmon evitano parte dei freeze UI.
- Il restore point pre-tweak è una rete di sicurezza aggiuntiva, non un rollback dei singoli valori.
- `procmon_verifier.py` associa eventi di registro riusciti al percorso completo e preserva i risultati precedenti quando un target non appare nella traccia.
- La tassonomia distingue osservazione runtime, consumer statico, table match, code reference, sola stringa e mancata osservazione.
- L’engine low-level aggiunge controlli di capacità e read-back per alcune scritture PCIe/xHCI.

Principi da conservare: lavoro fuori dal thread UI, timeout, read-back, identità stabile, evidenza stratificata e nessun verdetto negativo da una traccia incompleta.

### 5.3 Risultati Ghidra rilevanti

La cartella `ghidra_results` contiene 350 JSON: 340 hanno almeno un’occorrenza, per 2.436 occorrenze complessive. Il manifest collega i risultati di rete a:

- `tcpip.sys` versione `10.0.26100.8521`, SHA-256 `15034904b98c49b959c848f48ada109e86f6ee97ba5422ff9a6a61d848c6af39`;
- `afd.sys` versione `10.0.26100.8036`, SHA-256 `3358c34a140be8c497625d112d72a2f7209b532528e2d2869fdb5c1288c1ed42`.

Gli hash sono stati ricontrollati sui binari locali e coincidono con `imported_manifest.json`. I risultati più rilevanti per SockTuner sono:

| Target | Binario del corpus | Risultato statico | Conseguenza |
| --- | --- | --- | --- |
| `TcpAckFrequency` | `tcpip.sys` | `RTL_QUERY_REGISTRY_TABLE` confermata | rafforza la prova di consumo; non prova un beneficio |
| `TcpDelAckTicks` | `tcpip.sys` | `RTL_QUERY_REGISTRY_TABLE` confermata | chiude il vecchio gap “consumer non verificato” su questa build; resta Experimental/High risk |
| `FastSendDatagramThreshold` | `afd.sys` | `RTL_QUERY_REGISTRY_TABLE` confermata | prova che il nome è consumato; valore e beneficio restano non documentati |
| `DefaultReceiveWindow`, `DefaultSendWindow` | `afd.sys` | solo `TABLE_MATCH` | insufficiente per promozione |
| `MinRto`, `EnablePMTUDiscovery`, `TcpTimedWaitDelay` cercati in `tcpip.sys` | `tcpip.sys` | nessuna occorrenza nel target scelto | non è prova di non utilizzo: consumer, path o costruzione possono differire |

`verification_report.md` e `verified_targets.json` della nuova copia sono vuoti; non è inclusa una traccia Procmon che dimostri osservazioni live. `pinned_known_good.json` contiene dichiarazioni manuali, non evidenza riproducibile. Non importare automaticamente alcun verdict.

Conclusione concreta per SockTuner: aggiornare in futuro l’`EvidenceNote` di `TcpDelAckTicks` con la versione e l’hash sopra, mantenendo livello Experimental e rischio High finché documentazione e benchmark non stabiliscono semantica, range e vantaggio.

### 5.4 Problemi che restano bloccanti

- `IMOD-Test.py` tenta di disattivare la vulnerable driver blocklist e creare un servizio kernel WinRing0 con avvio automatico.
- Esegue scritture MSR/PCI/MMIO e tweak xHCI/NVMe che un read-back non rende sicuri o reversibili.
- `Nic.ps1` usa valori fissi, disabilita in massa i binding non IPv4/IPv6 e non acquisisce snapshot esatti.
- Il ramo MTU contiene un fallback logicamente incoerente; il flusso silent stampa `SUCCESS` anche quando alcune scritture sono fallite.
- L’app considera sufficiente la presenza del marker finale e può terminare brutalmente worker ancora attivi in chiusura.
- Il fallback RSS scrive direttamente il queue count nel registro quando il provider lo rifiuta.
- Profili inclusi sono specifici di una macchina/Realtek e non generalizzabili.
- Download/installazione automatica, elevazione, task pianificati e riavvii restano accoppiati a flussi senza transazione né rollback completo.

**Verdetto:** nessuna nuova operation o valore raccomandato da importare. La sola nuova informazione di prodotto è l’evidenza statica aggiuntiva; la sola funzione candidata resta RSS tipizzato già registrato dal delta 7.0.

## 6. Registro della pulizia

Il 10 settembre 2026, dopo la creazione e verifica di questo documento, sono state spostate nel Cestino senza cancellazione definitiva:

- `research/projects/`: 170 file, 159.914.579 byte;
- `research/scripts/`: 22 file, 1.473.895 byte;
- `research/tools/`: 6.126 file, 3.140.221.321 byte.

La verifica successiva ha confermato che le tre directory non esistono più nella working tree. Restano `research/README.md`, `links/`, `notes/` e `results/`.

Su richiesta del proprietario, anche `C:\Users\Lorenzo\Downloads\Zenit Latency Suite` è stato poi spostato nel Cestino: 6.091 file, 3.840.378.607 byte. Le informazioni necessarie sono conservate nel §5.
