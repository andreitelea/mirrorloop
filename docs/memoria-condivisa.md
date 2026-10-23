# Memoria condivisa tra ricevitore e plugin OBS

Come il ricevitore Windows passa immagini e audio già decompressi alla sorgente "MirrorLoop" dentro OBS. Lo scrittore è `windows/src/MirrorLoop.Ricevitore/BachecaObs.cs`; il lettore è il plugin in `obs/src/`.

> **Regola d'oro:** ricevitore e plugin devono scrivere e leggere **esattamente** questi byte. Ogni modifica al formato va fatta qui, nel codice C# e nel plugin insieme, alzando la **versione**.

## Idea

È una bacheca: il ricevitore appende, il plugin legge, nessuno dei due aspetta l'altro.

- Le **immagini** vanno in 3 cassetti usati a turno: il ricevitore scrive sempre nel cassetto più vecchio, il plugin legge l'ultimo completato. Se il plugin resta indietro perde immagini, non rallenta il ricevitore.
- L'**audio** va su un nastro circolare di 128 blocchi: il plugin legge in ordine tutti i blocchi nuovi dall'ultima volta. Se resta indietro di più di 128 blocchi, i più vecchi sono già stati sovrascritti e li salta.

Il ricevitore crea la memoria; il plugin la apre e, se non c'è ancora, riprova ogni secondo. Possono partire in qualsiasi ordine e il ricevitore si può chiudere e riaprire mentre OBS è aperto (vedi "Ricevitore riaperto").

## Nomi

| Oggetto | Nome |
| --- | --- |
| Memoria (file mapping di Windows, nel file di paging) | `Local\MirrorLoopOBS` |
| Evento "c'è qualcosa di nuovo" (auto-reset) | `Local\MirrorLoopOBS.Nuovo` |

`Local\` significa: visibili solo nella sessione di Windows dell'utente, dove girano sia il ricevitore sia OBS.

## Numeri e tempi

- Tutti i numeri sono **little-endian** (l'ordine nativo dei PC), interi senza segno: `u32` = 4 byte, `u64` = 8 byte.
- Gli **istanti** sono in nanosecondi dell'orologio di OBS: `QueryPerformanceCounter × 1 000 000 000 / QueryPerformanceFrequency`, cioè esattamente `os_gettime_ns()` di libobs su Windows. Il plugin li passa a OBS così come sono.

## Disposizione

```
┌─────────────────────┬──────────────────────────────┬──────────────────────────────┐
│ intestazione        │ 3 cassetti immagine          │ 128 blocchi audio            │
│ 4096 byte           │ 3 × 8 388 608 byte           │ 128 × 4096 byte              │
└─────────────────────┴──────────────────────────────┴──────────────────────────────┘
```

Dimensione totale: 4096 + 3 × 8 388 608 + 128 × 4096 = **25 694 208 byte** (~24,5 MiB). Il lettore non deve presupporre queste misure: le legge dall'intestazione.

### Intestazione (4096 byte)

| Posizione | Tipo | Campo | Significato |
| --- | --- | --- | --- |
| 0 | 4 byte | firma | i caratteri ASCII `MLOB` |
| 4 | u32 | versione | **1**. Un lettore che trova una versione diversa non legge nient'altro |
| 8 | u64 | dimensione totale | in byte |
| 16 | u64 | battito | istante dell'ultima scrittura o del battito periodico (almeno ogni 100 ms finché il ricevitore è aperto). Fermo da oltre 1 s = ricevitore chiuso o bloccato |
| 24 | u64 | generazione | cambia a ogni nuova trasmissione dall'iPhone e a ogni riapertura del ricevitore: il lettore riparte pulito (niente salti di istanti a cavallo) |
| 32 | u64 | immagini scritte | contatore che cresce sempre; 0 = nessuna immagine. L'ultima completata è nel cassetto `(immagini scritte − 1) mod numero cassetti` |
| 40 | u64 | blocchi audio scritti | contatore che cresce sempre; il blocco numero *n* (da 0) è nella posizione `n mod numero blocchi` |
| 48 | u32 | numero cassetti | 3 |
| 52 | u32 | dimensione cassetto | 8 388 608, compresi i 64 byte di intestazione del cassetto |
| 56 | u32 | numero blocchi audio | 128 |
| 60 | u32 | dimensione blocco audio | 4096, compresi i 64 byte di intestazione del blocco |
| 64 | u32 | processo | identificativo (PID) del ricevitore, per la diagnostica |
| 68 | — | riservato | zeri fino a 4096 |

### Cassetto immagine

| Posizione | Tipo | Campo | Significato |
| --- | --- | --- | --- |
| 0 | u64 | sequenza | vedi "Letture sicure" |
| 8 | u64 | numero | quale immagine è (da 1, come il contatore "immagini scritte") |
| 16 | u64 | istante | istante di cattura, già nel tempo di OBS |
| 24 | u64 | generazione | la generazione al momento della scrittura |
| 32 | u32 | larghezza | in pixel, pari |
| 36 | u32 | altezza | in pixel, pari |
| 40 | u32 | passo Y | byte per riga del piano Y |
| 44 | u32 | passo UV | byte per riga del piano UV |
| 48 | u32 | formato | **1 = NV12** |
| 52 | u32 | colore | **1 = BT.709, gamma limitata** (16–235) |
| 56 | u32 | byte | byte di dati validi |
| 60 | — | riservato | |
| 64 | | dati | piano Y (altezza righe), subito dopo il piano UV (altezza / 2 righe) |

L'immagine è già **dritta**: la rotazione la applica il ricevitore prima di scrivere. Spazio per i dati: 8 388 544 byte, abbastanza per un NV12 fino a ~5,59 milioni di pixel (lo schermo intero di un iPhone 17 Pro Max, 1320 × 2868, ne ha 3,79).

### Blocco audio

| Posizione | Tipo | Campo | Significato |
| --- | --- | --- | --- |
| 0 | u64 | sequenza | vedi "Letture sicure" |
| 8 | u64 | numero | quale blocco è (da 0, come nella posizione `n mod numero blocchi`) |
| 16 | u64 | istante | istante del primo campione, nel tempo di OBS |
| 24 | u64 | generazione | la generazione al momento della scrittura |
| 32 | u32 | campioni | campioni per canale nel blocco |
| 36 | u32 | canali | 2 = stereo |
| 40 | u32 | frequenza | campioni al secondo, es. 48 000 |
| 44 | u32 | formato | **1 = PCM 16 bit con segno, canali interlacciati** (sinistro, destro, sinistro…) |
| 48 | u32 | byte | byte di dati validi = campioni × canali × 2 |
| 52 | — | riservato | |
| 64 | | dati | al massimo 4032 byte (21 ms di stereo a 48 kHz) |

Un pezzo di audio più lungo di un blocco viene diviso dal ricevitore in più blocchi consecutivi, con gli istanti che avanzano di conseguenza.

## Letture sicure (sequenza)

Il ricevitore può riscrivere un cassetto o un blocco mentre il plugin lo sta copiando. Per accorgersene si usa la tecnica standard del *seqlock*:

**Scrittura** (ricevitore):
1. sequenza += 1 (diventa dispari: "sto scrivendo");
2. scrive campi e dati;
3. sequenza += 1 (torna pari: "finito");
4. aggiorna il contatore nell'intestazione ("immagini scritte" o "blocchi audio scritti") e il battito;
5. segnala l'evento.

Ogni passo è visibile agli altri processi solo dopo i precedenti (barriere di memoria).

**Lettura** (plugin):
1. legge il contatore nell'intestazione e sceglie il cassetto o il blocco;
2. legge la sequenza: se è dispari, il ricevitore sta scrivendo → riprova più tardi;
3. controlla che il **numero** sia quello atteso;
4. copia campi e dati;
5. rilegge la sequenza: se è cambiata, la copia può essere mista → la butta.

## Avviso

Dopo ogni scrittura il ricevitore segnala l'evento `Local\MirrorLoopOBS.Nuovo`. Il plugin lo aspetta con un limite di 10 ms e poi controlla comunque i contatori: l'evento è auto-reset e sveglia un solo lettore, quindi con più sorgenti MirrorLoop le altre se ne accorgono al massimo 10 ms dopo.

## Ricevitore riaperto

Finché il plugin tiene aperta la memoria, Windows la conserva anche se il ricevitore si chiude. Quando il ricevitore la riapre e trova firma, versione e misure giuste, **non azzera i contatori** (il lettore vedrebbe numeri tornare indietro): continua da dove erano e aumenta la generazione. Se la firma o la versione non tornano, la inizializza da capo.
