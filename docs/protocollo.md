# Protocollo di trasmissione

Come viaggiano i dati dal mittente (l'app iPhone, oppure `MittenteTest` per le prove sul PC) al ricevitore Windows. L'implementazione per iPhone è in `ios/MirrorLoop/Trasmissione.swift`. L'implementazione di riferimento è `windows/src/MirrorLoop.Protocollo`.

> **Regola d'oro:** mittente e ricevitore devono produrre e leggere **esattamente** questi byte. Ogni modifica al formato va fatta qui, nel codice C# e nell'app iPhone insieme.

## Trasporto

Il ricevitore ascolta sulla porta **7450** in due modi; il mittente sceglie quale usare. Un solo mittente alla volta: una connessione nuova chiude la precedente.

- **TCP:** una connessione per trasmissione, tutti i messaggi in un'unica fila. Si disattiva l'algoritmo di Nagle (`NoDelay`), per spedire subito ogni frame.
- **QUIC** (su UDP, sempre cifrato con TLS 1.3): protocollo applicativo (ALPN) `mirrorloop/1`. Il mittente apre tre **corsie** indipendenti, così una perdita in una non blocca le altre:
  - **controllo**: ping (ricevitore → mittente) e pong (mittente → ricevitore);
  - **video**: frame video (tipo 1);
  - **audio**: pacchetti audio (tipo 2).

  Ogni corsia è un flusso QUIC (*stream*) e comincia sempre con un messaggio di **apertura corsia** (tipo 5) che dice il suo ruolo e a quale trasmissione appartiene. Le corsie possono viaggiare in connessioni QUIC separate (come fa l'app iPhone: una connessione per corsia) oppure nella stessa: il ricevitore le raggruppa solo in base al **numero di trasmissione**. Il ricevitore scrive (i ping) solo sulla corsia di controllo, che quindi deve essere bidirezionale; le altre possono essere bidirezionali o unidirezionali.

  Il certificato del ricevitore è autofirmato. Su ogni corsia i messaggi hanno lo stesso formato descritto qui sotto.

## Struttura di ogni messaggio

TCP è un flusso continuo, quindi ogni messaggio comincia con la sua lunghezza (vedi `concetti.md`, "TCP è un flusso").

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
| altri | Riservati | Il ricevitore **li salta senza errore**, così le versioni future potranno aggiungerne senza rompere i ricevitori più vecchi |

## Tipo 1 · Frame video

```
┌──────────┬──────────┬────────────────────────┬─────────────────────┐
│ tipo = 1 │ flag     │ istante di cattura     │ dati H.264          │
│ 1 byte   │ 1 byte   │ 8 byte                 │ almeno 1 byte       │
└──────────┴──────────┴────────────────────────┴─────────────────────┘
```

- **flag:** bit 0 = **keyframe** (1 se il frame si decodifica da solo). Gli altri bit sono riservati e vanno lasciati a 0.
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
- se il numero è quello della trasmissione in corso, aggiunge la corsia alla trasmissione;
- se il numero è nuovo, chiude la trasmissione in corso (tutte le sue corsie e connessioni) e ne comincia una nuova;
- quando una corsia di una trasmissione si chiude, chiude tutta la trasmissione: il mittente si ricollegherà con un numero nuovo.

Esempio verificato da un test automatico (`IlFormatoDellAperturaCorsiaSulFiloEQuelloDocumentato`): la corsia video della trasmissione `0x0102030405060708` comincia con `00 00 00 0A 05 02 01 02 03 04 05 06 07 08`.

## Comportamento del ricevitore

- Si collega un mittente: il ricevitore **scarta i frame finché non arriva un keyframe**.
- La connessione si chiude tra un messaggio e l'altro: fine normale della trasmissione.
- La connessione si chiude a metà di un messaggio, oppure arriva una lunghezza non valida: errore, e la connessione si chiude.
