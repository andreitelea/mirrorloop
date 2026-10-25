# Protocollo di trasmissione

Come viaggiano i dati dal mittente (l'app iPhone, oppure `MittenteTest` per le prove sul PC) al ricevitore Windows. L'implementazione per iPhone è in `ios/MirrorLoop/Trasmissione.swift`. L'implementazione di riferimento è `windows/src/MirrorLoop.Protocollo`.

> **Regola d'oro:** mittente e ricevitore devono produrre e leggere **esattamente** questi byte. Ogni modifica al formato va fatta qui, nel codice C# e nell'app iPhone insieme.

## Trasporto

Il ricevitore ascolta sulla porta **7450** in **QUIC** (su UDP, sempre cifrato con TLS 1.3), protocollo applicativo (ALPN) `mirrorloop/1`. Un solo mittente alla volta: una trasmissione nuova chiude la precedente. (Fino al 25/10/2026 c'era anche TCP, tolto perché non cifrato e senza vantaggi misurati.)

Il mittente apre tre **corsie** indipendenti, così una perdita in una non blocca le altre:

- **controllo**: ping (ricevitore → mittente) e pong (mittente → ricevitore);
- **video**: frame video (tipo 1);
- **audio**: pacchetti audio (tipo 2).

Ogni corsia è un flusso QUIC (*stream*) e comincia sempre con un messaggio di **apertura corsia** (tipo 5) che dice il suo ruolo e a quale trasmissione appartiene. Le corsie possono viaggiare in connessioni QUIC separate (come fa l'app iPhone: una connessione per corsia) oppure nella stessa: il ricevitore le raggruppa solo in base al **numero di trasmissione**. Il ricevitore scrive (i ping) solo sulla corsia di controllo, che quindi deve essere bidirezionale; le altre possono essere bidirezionali o unidirezionali.

Il certificato del ricevitore è autofirmato. Su ogni corsia i messaggi hanno lo stesso formato descritto qui sotto.

## Struttura di ogni messaggio

Una corsia QUIC è un flusso continuo di byte, come TCP, quindi ogni messaggio comincia con la sua lunghezza (vedi `concetti.md`, "TCP è un flusso").

```
┌──────────────┬──────────┬───────────────────────────┐
│ lunghezza    │ tipo     │ contenuto                 │
│ 4 byte       │ 1 byte   │ lunghezza − 1 byte        │
└──────────────┴──────────┴───────────────────────────┘
```

- **lunghezza:** intero con segno a 32 bit, **big-endian**. Conta *tipo + contenuto*, quindi non include i 4 byte della lunghezza stessa. Valori ammessi: da 1 a 8 388 608 (8 MB). Fuori da questo intervallo il ricevitore chiude la connessione.
- **tipo:** che cosa contiene il messaggio (tabella sotto).

Tutti i numeri sono **big-endian** ("network byte order").

### Tipi di messaggio

| Tipo | Nome | Stato |
| --- | --- | --- |
| 1 | Frame video | In uso |
| 2 | Audio | In uso dalla Fase 4 |
| 3 | Ping (dal ricevitore al mittente) | In uso dalla Fase 5 |
| 4 | Pong (dal mittente al ricevitore) | In uso dalla Fase 5 |
| 5 | Apertura corsia (solo QUIC) | In uso dalla Fase 5 |
| 6–9 | Abbinamento: presentazione, sfida, prova, esito | Fase 7 (protocollo `mirrorloop/2`) |
| altri | Riservati | Il ricevitore **li salta senza errore**, così le versioni future potranno aggiungerne senza rompere i ricevitori più vecchi |

## Tipo 1 · Frame video

```
┌──────────┬──────────┬────────────────────────┬─────────────────────┐
│ tipo = 1 │ flag     │ istante di cattura     │ dati H.264          │
│ 1 byte   │ 1 byte   │ 8 byte                 │ almeno 1 byte       │
└──────────┴──────────┴────────────────────────┴─────────────────────┘
```

- **flag:**
  - bit 0 = **keyframe** (1 se il frame si decodifica da solo);
  - bit 1 e 2 = **rotazione**: di quanto girare l'immagine **in senso orario** per mostrarla dritta, a passi di 90° (valore 0 = nessuna, 1 = 90°, 2 = 180°, 3 = 270°). L'iPhone cattura sempre nel verso del pannello: con il telefono in orizzontale il contenuto arriva coricato, e la rotazione dice come raddrizzarlo. Vale per il frame che la porta; può cambiare da un frame all'altro senza bisogno di un keyframe;
  - gli altri bit sono riservati e vanno lasciati a 0.
- **istante di cattura:** intero con segno a 64 bit, big-endian, in **microsecondi dal 1° gennaio 1970 UTC**. Serve a misurare la latenza. Ha senso solo se i due orologi sono allineati: è vero sullo stesso PC, mentre tra iPhone e PC andrà gestito (Fase 5).
- **dati H.264:** **un frame intero** (in gergo *access unit*) in formato **Annex B**, cioè con i codici di inizio `00 00 00 01` davanti a ogni pezzo (*NAL unit*). Ogni keyframe deve includere anche **SPS e PPS**, le "istruzioni per il decoder", così chi si collega a metà trasmissione può partire dal primo keyframe che riceve. Gli **AUD** (*Access Unit Delimiter*, NAL di tipo 9) non servono, perché ogni messaggio contiene già un frame intero: il mittente li toglie.

### Esempio byte per byte

Un keyframe con istante `0x0102030405060708` e due byte di dati `AB CD`:

```
00 00 00 0C   lunghezza = 12 (1 tipo + 1 flag + 8 istante + 2 dati)
01            tipo: frame video
01            flag: keyframe
01 02 03 04 05 06 07 08   istante di cattura
AB CD         dati H.264
```

Questo esempio è verificato da un test automatico (`IlFormatoSulFiloEQuelloDocumentato`).

Lo stesso keyframe da girare di 90° in senso orario ha flag `03` (bit 0 e bit 1); verificato da `LaRotazioneStaNeiBit1E2DelFlag`.

## Tipo 2 · Audio

```
┌──────────┬──────────┬──────────┬────────────┬────────────────────┬─────────────────┐
│ tipo = 2 │ codifica │ canali   │ frequenza  │ istante di cattura │ campioni        │
│ 1 byte   │ 1 byte   │ 1 byte   │ 4 byte     │ 8 byte             │ almeno 1 byte   │
└──────────┴──────────┴──────────┴────────────┴────────────────────┴─────────────────┘
```

- **codifica:** come sono scritti i campioni.

  | Valore | Codifica |
  | --- | --- |
  | 1 | **PCM 16 bit**: non compresso, interi con segno a 16 bit **little-endian** (come nei file WAV), canali **intercalati** (sinistro, destro, sinistro, destro…) |
  | altri | Riservati (per esempio Opus in futuro). Il ricevitore **ignora** i pacchetti con una codifica che non conosce |

- **canali:** 1 = mono, 2 = stereo. Zero non è ammesso.
- **frequenza:** campioni al secondo per canale, intero con segno a 32 bit, big-endian (per esempio 48 000).
- **istante di cattura:** come per il video (microsecondi dal 1° gennaio 1970 UTC, 64 bit, big-endian), riferito al **primo campione** del pacchetto. Audio e video usano lo **stesso orologio** del mittente: è ciò che permette di sincronizzarli.
- **campioni:** con il PCM 16 bit la lunghezza deve essere un multiplo di 2 × canali byte, altrimenti il messaggio è considerato corrotto.

Attenzione: i numeri dell'intestazione sono big-endian come nel resto del protocollo, mentre i **campioni PCM sono little-endian**, il formato naturale delle schede audio e dei file WAV.

### Esempio byte per byte

Due campioni (uno sinistro `0x2010`, uno destro `0x4030`) in stereo a 48 kHz, con istante `0x0102030405060708`:

```
00 00 00 13   lunghezza = 19 (15 di intestazione + 4 byte di campioni)
02            tipo: audio
01            codifica: PCM 16 bit
02            canali: stereo
00 00 BB 80   frequenza = 48 000
01 02 03 04 05 06 07 08   istante di cattura
10 20 30 40   campioni (little-endian)
```

Questo esempio è verificato da un test automatico (`IlFormatoAudioSulFiloEQuelloDocumentato`).

## Tipi 3 e 4 · Ping e pong (sincronizzazione degli orologi)

È l'unico traffico nella direzione **ricevitore → mittente**. Serve a misurare di quanto l'orologio del mittente è spostato rispetto a quello del ricevitore, con il metodo dei server dell'ora di Internet (NTP), così le latenze calcolate dal ricevitore sono vere anche tra due dispositivi diversi.

```
Ping (ricevitore → mittente):
┌──────────┬──────────┬──────────────┐
│ tipo = 3 │ numero   │ T1           │
│ 1 byte   │ 4 byte   │ 8 byte       │
└──────────┴──────────┴──────────────┘

Pong (mittente → ricevitore):
┌──────────┬──────────┬──────────────┬──────────────┬──────────────┐
│ tipo = 4 │ numero   │ T1           │ T2           │ T3           │
│ 1 byte   │ 4 byte   │ 8 byte       │ 8 byte       │ 8 byte       │
└──────────┴──────────┴──────────────┴──────────────┴──────────────┘
```

- **numero:** progressivo scelto dal ricevitore (intero con segno a 32 bit, big-endian); il pong lo ricopia.
- **T1:** ora del ricevitore alla partenza del ping; il pong la ricopia identica.
- **T2:** ora del mittente all'arrivo del ping. **T3:** ora del mittente alla partenza del pong.
- Tutte le ore sono in microsecondi dal 1° gennaio 1970 UTC (64 bit, big-endian). T2 e T3 usano **lo stesso orologio degli istanti di cattura** di video e audio.
- Il mittente risponde a ogni ping appena lo riceve. Un mittente che non conosce il tipo 3 lo ignora: il ricevitore allora non può sincronizzare gli orologi e lo segnala.
- Lunghezze fisse: 13 byte per il ping, 29 per il pong (escluso il prefisso). Una lunghezza diversa è un errore.

Con T4 = ora del ricevitore all'arrivo del pong:

- **scarto** (ora del mittente − ora del ricevitore) = ((T2 − T1) + (T3 − T4)) / 2
- **andata e ritorno** = (T4 − T1) − (T3 − T2)

Il calcolo è esatto se andata e ritorno durano uguale. Il pong può restare in coda dietro ai frame video: il ricevitore manda un ping al secondo e, tra le ultime 30 misure, usa quella con l'andata e ritorno più breve.

Esempio verificato da un test automatico (`IlFormatoDiPingEPongSulFiloEQuelloDocumentato`): un ping con numero `0x0A0B0C0D` e T1 `0x0102030405060708` è `00 00 00 0D 03 0A 0B 0C 0D 01 02 03 04 05 06 07 08`.

## Tipo 5 · Apertura corsia (solo QUIC)

Il primo messaggio di ogni corsia QUIC. Serve anche perché in QUIC una corsia esiste per l'altro lato solo dopo il primo dato: senza, il ricevitore non saprebbe dove mandare i ping.

```
┌──────────┬──────────┬──────────────────────────┐
│ tipo = 5 │ ruolo    │ numero di trasmissione   │
│ 1 byte   │ 1 byte   │ 8 byte                   │
└──────────┴──────────┴──────────────────────────┘
```

- **ruolo:** 1 = controllo, 2 = video, 3 = audio. Altri valori sono riservati: il ricevitore tratta la corsia come una corsia di dati (legge i messaggi, non manda ping).
- **numero di trasmissione:** 64 bit, big-endian, scelto **a caso** dal mittente a ogni nuovo tentativo di collegamento e uguale su tutte le corsie di quel tentativo.
- Lunghezza fissa: 10 byte (escluso il prefisso). Una lunghezza diversa è un errore.

Il ricevitore, per ogni corsia nuova:

- se il primo messaggio non arriva entro 5 secondi, o non è un'apertura corsia, chiude la corsia;
- aspetta che la trasmissione sia accettata dall'abbinamento (tipi 6–9, sulla corsia di controllo);
- se il numero è quello della trasmissione in corso, aggiunge la corsia alla trasmissione;
- se il numero è nuovo, chiude la trasmissione in corso (tutte le sue corsie e connessioni) e ne comincia una nuova;
- quando una corsia di una trasmissione si chiude, chiude tutta la trasmissione: il mittente si ricollegherà con un numero nuovo.

Esempio verificato da un test automatico (`IlFormatoDellAperturaCorsiaSulFiloEQuelloDocumentato`): la corsia video della trasmissione `0x0102030405060708` comincia con `00 00 00 0A 05 02 01 02 03 04 05 06 07 08`.

## Tipi 6–9 · Abbinamento (protocollo `mirrorloop/2`)

Perché nessuno, sulla stessa rete, possa fingersi il PC (e vedere lo schermo dell'iPhone) o fingersi un iPhone (e mandare immagini in diretta). Il PC si riconosce dall'**impronta** del suo certificato QUIC (SHA-256 del certificato in formato DER); l'iPhone da una sua **chiave** ECDSA P-256, creata al primo avvio e custodita nel portachiavi. Al primo incontro i due schermi mostrano lo stesso **codice di 6 cifre** e una persona conferma su tutti e due; dopo, si riconoscono da soli. I calcoli sono in `windows/src/MirrorLoop.Protocollo/Abbinamento.cs`.

Per distinguere le versioni, il nome del protocollo QUIC (ALPN) diventa `mirrorloop/2`: un'app o un ricevitore della versione precedente si fermano subito alla connessione.

### Sequenza, sulla corsia di controllo, subito dopo l'apertura (tipo 5)

1. **iPhone → PC · presentazione (6):** il **modo** (0 = ricollegamento a un PC che l'iPhone conosce già, 1 = nuovo abbinamento), la chiave pubblica e l'**impegno** = SHA-256(`"MirrorLoop impegno v1"` ‖ chiave ‖ numero dell'iPhone), dove il numero dell'iPhone sono 32 byte casuali nuovi a ogni collegamento.
2. **PC → iPhone · sfida (7):** il numero del PC, 32 byte casuali nuovi a ogni collegamento.
3. **iPhone → PC · prova (8):** il numero dell'iPhone (ora rivelato) e la **firma** ECDSA P-256 con SHA-256 di `"MirrorLoop prova v1"` ‖ impronta del PC ‖ chiave ‖ numero del PC ‖ numero dell'iPhone. L'impronta è quella del certificato che l'iPhone ha visto nella connessione.
4. **PC → iPhone · esito (9):** il PC controlla impegno e firma, poi:
   - modo 0 e iPhone già abbinato → **accettato** (0);
   - iPhone nuovo, oppure modo 1 → **in attesa** (1): il PC mostra il codice e chiede di consentire o rifiutare; poi manda un secondo esito, accettato o rifiutato. Il modo 1 serve quando l'iPhone non conosce il PC ma il PC conosce l'iPhone (per esempio dopo una reinstallazione dell'app, che cancella i PC conosciuti ma non la chiave nel portachiavi): senza, il PC accetterebbe subito e non ci sarebbe un codice da confrontare;
   - impegno o firma sbagliati, richiesta rifiutata o scaduta → **rifiutato** (2), e il PC chiude la trasmissione.

Il **codice** si calcola da SHA-256(`"MirrorLoop codice v1"` ‖ impronta ‖ chiave ‖ numero del PC ‖ numero dell'iPhone): i primi 4 byte come intero big-endian senza segno, modulo 1 000 000, scritti con 6 cifre (zeri iniziali compresi). Se in mezzo ci fosse un impostore, i due schermi mostrerebbero codici diversi; l'impegno gli impedisce di provare chiavi finché coincidono per caso (gli resta una possibilità su un milione).

Le etichette tra virgolette sono in ASCII, senza terminatore; ‖ vuol dire "seguito da".

L'iPhone memorizza l'impronta del PC solo dopo la conferma della persona **e** l'esito accettato; le volte successive, un'impronta diversa per lo stesso PC è un allarme, non un nuovo abbinamento automatico. Se l'iPhone riceve "in attesa" in un ricollegamento (il PC lo ha dimenticato), mostra anche lui il codice e chiede conferma. Video e audio partono solo dopo l'esito accettato.

Il ricevitore:

- apre la trasmissione (azzera code e statistiche, chiude quella precedente, manda i ping) solo dopo aver accettato la corsia di controllo: chi non è abbinato non interrompe una trasmissione in corso;
- tiene in sospeso le corsie video e audio finché la corsia di controllo con lo stesso numero non è accettata, e le chiude senza leggerle se viene rifiutata;
- aspetta ogni messaggio dell'abbinamento al massimo 5 secondi e la conferma della persona al massimo 60;
- gestisce una sola richiesta di conferma alla volta: le altre, nel frattempo, sono rifiutate subito;
- memorizza i telefoni abbinati in `%LOCALAPPDATA%\MirrorLoop\telefoni-abbinati.json`.

### Formato

```
presentazione:  tipo = 6 (1) │ modo (1) │ chiave (65) │ impegno (32) │ nome (0–64, UTF-8)
sfida:          tipo = 7 (1) │ numero del PC (32)
prova:          tipo = 8 (1) │ numero dell'iPhone (32) │ firma (64)
esito:          tipo = 9 (1) │ esito (1): 0 accettato, 1 in attesa, 2 rifiutato
```

- **chiave:** punto P-256 non compresso, `04` ‖ X (32 byte) ‖ Y (32 byte), come `x963Representation` di CryptoKit.
- **firma:** r ‖ s, 32 byte ciascuno, come `rawRepresentation` di CryptoKit.
- **modo:** 0 o 1; altri valori sono un errore.
- **nome:** da mostrare sul PC (iOS dà alle app solo "iPhone"); oltre 64 byte è un errore.
- Lunghezze fisse per sfida (33), prova (97) ed esito (2); un esito diverso da 0, 1, 2 è un errore.

### Esempio con valori fissi

Verificato da un test automatico (`LEsempioDelDocumentoTorna`) e ricalcolato a parte in Python. L'app deve ottenere gli stessi impegno e codice; la firma ECDSA cambia a ogni calcolo, ma questa deve risultare valida.

| | Valore (esadecimale) |
| --- | --- |
| chiave privata (solo per l'esempio) | `bbb66c5be08c2a5c1fe3ec756f68517e31499ffb115bb8fbf526415d10ea26e9` = SHA-256(`"MirrorLoop chiave di prova"`) |
| chiave | `0402e1ecbb59b6a867788ad406194e9d186d62a8ea3ae31b441e5df2cdbeb5074647f2ee5bcdd4948f2748373ace3a10fa44233730c2d4353437edbf6ae98504f5` |
| impronta | `45bd8dfef7caf4232b2c31bfc249e6a5dd1743c727291408d2f0a05ebe0db347` = SHA-256(`"certificato di prova"`) |
| numero del PC | 32 byte `11` |
| numero dell'iPhone | 32 byte `22` |
| impegno | `ba04f4ce2586799326edcb32db1e6e33dfc33e22534f243e167387de0f8e6c5e` |
| codice | **713021** |
| una firma valida | `e754f9bd671aaa7774779c04e57639fea1c9080b5d966ebc5a39641d7e607eb4be622936d4a8e52527dddbe66b7da6fd7d4817d30f047b6a62521dfc8f5191f9` |

## Comportamento del ricevitore

- Si collega un mittente: il ricevitore **scarta i frame finché non arriva un keyframe**.
- La connessione si chiude tra un messaggio e l'altro: fine normale della trasmissione.
- La connessione si chiude a metà di un messaggio, oppure arriva una lunghezza non valida: errore, e la connessione si chiude.
