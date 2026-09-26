# SockTuner — handoff rapido

Aggiornato: **2026-09-26**
Workspace: `C:\Users\Lorenzo\Documents\Projects\SockTuner`
Branch osservato: `test/capability-archive-gate`
HEAD osservato: `df40eeae8711a4cb0fa9cb6c33b4b8b74d092e51` (`docs: archive Zenit research findings`)

## Leggere prima di agire

1. Leggere `AGENTS.md`: contiene i vincoli di sicurezza e il workflow obbligatorio.
2. Non eseguire script o binari sotto `research/`; quel materiale è solo riferimento statico.
3. Non effettuare mutazioni di rete sul computer di sviluppo. I test di scrittura usano fake/in-memory;
   quelli Windows reali richiedono una VM usa-e-getta e opt-in esplicito.
4. Preservare le modifiche esistenti. Non usare reset/checkout distruttivi per “pulire” il working tree.

## Stato del prodotto

Il piano implementativo principale è sostanzialmente completo:

- tutti i workstream W1–W10 di `docs/DELIVERY_PLAN.md` risultano completati;
- gli Step 1–6, 8 e 9 della roadmap sono completi;
- Step 7a è completo e Step 7b resta in alpha come attività continuativa di validazione hardware;
- Step 10 (private beta/1.0) dipende soprattutto da prove esterne, non da altro codice ordinario.

Non c'è una feature applicativa lasciata a metà. La coda reale è:

1. ampliare il corpus Step 7b quando arrivano probe da hardware reale;
2. chiudere Step 10 con certificato di firma, macchine Windows 10/11 reali, DPI e locale EN/IT,
   screen reader, soak test e recovery drill in VM;
3. lasciare il trasporto OpenWrt SSH come incremento separato finché non sono definiti dipendenza,
   gestione delle chiavi e review della superficie di sicurezza.

Fonti autorevoli: `docs/ROADMAP.md` e `docs/DELIVERY_PLAN.md`.

## Lavoro appena svolto: nuovo probe Realtek

Sul Desktop erano presenti due JSON:

- `socktuner-probe-20260915-064213.json` — utile direttamente alla capability matrix;
- `NeuroTune-HardwareReport-20260915-134216.json` — utile solo come corroborazione hardware.

Il probe SockTuner è stato elaborato con lo strumento già esistente:

```powershell
python tools\build-probe-archive.py 'C:\Users\Lorenzo\Desktop'
```

Risultato intenzionale:

- aggiunto `alpha-tester-output/Realtek-2-5GbE-10.79.50.1003.json`;
- archiviato il report redatto originale in
  `alpha-tester-output/reports/socktuner-probe-20260915-064213.json`;
- aggiornato `alpha-tester-output/INDEX.md` da 8 a 9 adattatori fisici e da 77 a 82 keyword;
- caratterizzate 33 proprietà NDIS e 33 capability strutturate per Realtek RTL8125,
  driver `10.79.50.1003`, Windows `10.0.26200.0`.

La copia del probe sul Desktop è stata eliminata **solo dopo** avere verificato che il suo SHA-256
fosse identico alla copia nel progetto:

```text
D47E5C28705261BDD049D0B9A302A7DC1D41BF23F6C1D81A3594FEEBEEF86722
```

Il report NeuroTune non è stato copiato nel progetto e resta sul Desktop. Conferma la stessa NIC,
il driver, il PCI ID e il supporto MSI, oltre a una CPU Intel i5-10600K 6C/12T in un solo processor
group. Non contiene però vincoli sufficienti per aggiungere una nuova regolazione scrivibile alla
matrice, quindi non è stata inventata alcuna feature.

## Incremento capability matrix completato

Il gate dell'archivio ha rilevato cinque keyword standard pubblicizzate dal nuovo driver ma non
ancora caratterizzate. Il catalogo centrale ora classifica:

- `*RSS` e `*NumRssQueues` come controlli di latenza/throughput a rischio medio;
- `*SelectiveSuspend` e `*SSIdleTimeout` come controlli di latenza/potenza a rischio medio;
- `*ModernStandbyWoLMagicPacket` come controllo wake/potenza a rischio basso.

Le classificazioni sono coperte dai test e riportate nei record derivati dell'archivio. Il report
redatto originale resta invariato come provenienza. Oltre al nuovo record Realtek, l'entry Hyper-V
preesistente è stata allineata per le due keyword RSS che contiene. Il generatore preserva queste
classificazioni revisionate quando un report raw più vecchio riporta ancora `Unknown`; la modalità
read-only `python tools/build-probe-archive.py --check` verifica che la rigenerazione non produca diff.

`git status` può mostrare altri JSON tracciati sotto `alpha-tester-output/` come modificati per solo
rumore di line ending. Non includerli alla cieca: `git diff --name-only` mostra le differenze di
contenuto reali.

## Verifiche eseguite

Dopo l'aggiunta del probe e la caratterizzazione delle nuove keyword:

- `dotnet restore`: riuscito, progetti già aggiornati;
- `dotnet build --no-restore`: riuscito, 0 warning e 0 errori;
- `dotnet test --no-build`: 763 passati, 12 live test esclusi, 775 totali;
- `dotnet format --verify-no-changes`: riuscito;
- `python tools/build-probe-archive.py --check`: archivio rigenerabile senza diff;
- `git diff --check`: riuscito;
- entrambi i nuovi JSON sono stati parsati correttamente;
- nessun test Windows live o mutazione di rete è stato eseguito.

## Analisi Zenit appena conclusa

La nuova cartella `research/tools/Zenit/Zenit - Jackpot Latency Suite/` è stata confrontata
staticamente con le versioni precedenti. Non è stato eseguito nulla sotto `research/`.

Il report completo è `docs/JACKPOTS_ZENIT_7.0_LATENCY_SUITE_DELTA.md`. Conclusione:

- quasi tutto è già presente in SockTuner in forma più sicura, è fuori scope o non ha evidenza;
- non copiare preset RSS statici, “unlock” via registro, valori `Ndi\Params` inventati, reset
  Winsock/IP, disabilitazioni massive dei binding o i verdetti deboli del verifier Ghidra;
- l'unico candidato futuro interessante è il controllo nativo della topologia RSS tramite
  `MSFT_NetAdapterRssSettingData`.

Quel candidato **non è una fase attiva** e non serve per chiudere la release corrente. Se verrà
richiesto, la versione minima sicura è:

1. prima ampliare l'inventario read-only con indirection table, RSS processor array, numero di
   interrupt, MSI/MSI-X;
2. poi introdurre un'unica impostazione composita tipizzata che snapshotta e ripristina l'intera
   policy RSS di un adattatore preciso;
3. validare processor groups, topologia e limiti live, senza mappe CPU basate sul solo core count;
4. verificare apply e rollback in VM e rileggere anche la tabella di indirizzamento risultante.

## Direzione architetturale da preservare

- C#/.NET 10 LTS, WPF, x64-first, Windows 10/11.
- API native/CIM prima di tutto; nessuna dipendenza PowerShell a runtime e nessuna shell arbitraria.
- UI normalmente non elevata; worker elevato tipizzato e allowlisted per le scritture.
- Ogni setting scrivibile deve avere snapshot, validazione live, read-back, audit e rollback esatto.
- Le capability NIC derivano da ciò che il driver pubblicizza; proprietà assenti non si creano e
  valori non pubblicizzati non si indovinano.
- Diagnosi, raccolta e remediation restano separati; la remediation propone modifiche e passa dal
  transaction engine esistente.
- Preferire incrementi piccoli. Non ampliare lo scope solo perché una ricerca esterna elenca molti
  tweak.

## Avvio rapido per il prossimo agente

```powershell
Get-Content AGENTS.md
Get-Content docs\HANDOFF.md
git status --short --untracked-files=all
```

Obiettivo successivo: continuare Step 7b solo quando arrivano probe da altro hardware reale, oppure
chiudere le prove esterne di Step 10. Non iniziare una riscrittura o implementare automaticamente
il candidato RSS composito.
