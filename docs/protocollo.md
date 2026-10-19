# Protocollo di trasmissione

Come viaggiano i dati dal mittente (oggi `MittenteTest` sul PC, domani l'app iPhone) al ricevitore Windows. L'implementazione di riferimento è `windows/src/MirrorLoop.Protocollo`.

> **Regola d'oro:** mittente e ricevitore devono produrre e leggere **esattamente** questi byte. Ogni modifica al formato va fatta qui, nel codice C# e nell'app iPhone insieme.

## Trasporto

- **TCP**, una connessione per trasmissione.
- Il ricevitore resta in ascolto, il mittente si collega.
- Su ogni socket si disattiva l'algoritmo di Nagle (`NoDelay`), per spedire subito ogni frame.

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
| altri | Riservati | Il ricevitore **li salta senza errore**, così le versioni future potranno aggiungerne (per esempio l'audio nella Fase 4) senza rompere i ricevitori più vecchi |

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

## Comportamento del ricevitore

- Si collega un mittente: il ricevitore **scarta i frame finché non arriva un keyframe**.
- La connessione si chiude tra un messaggio e l'altro: fine normale della trasmissione.
- La connessione si chiude a metà di un messaggio, oppure arriva una lunghezza non valida: errore, e la connessione si chiude.
