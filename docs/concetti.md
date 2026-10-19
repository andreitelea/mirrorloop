# Concetti di base

Le idee che servono per capire come funziona MirrorLoop. Ogni concetto ha un'analogia e, dove serve, i numeri veri.

Ricordiamo l'architettura: è una piccola emittente TV. L'iPhone è la telecamera con il trasmettitore, il PC è il televisore, OBS è la regia.

---

## Parte 1 · Il video

### Frame

Un **frame** è una singola immagine fissa dello schermo.

> **Analogia:** un flipbook, il blocchetto di fogli che sfogliati velocemente sembrano un cartone animato. Ogni foglio è un frame.

Un frame è fatto di **pixel**, i puntini colorati che compongono l'immagine. La **risoluzione** dice quanti pixel ci sono: 1920 × 1080 (detta "1080p") vuol dire 1920 pixel in larghezza e 1080 in altezza, cioè circa 2 milioni di pixel.

### fps (frame al secondo)

Gli **fps** dicono quanti frame si mostrano ogni secondo.

> **Analogia:** quanto velocemente sfogli il flipbook. Pochi fogli al secondo e il movimento "scatta"; tanti fogli e diventa fluido.

| fps | Effetto |
| --- | --- |
| 24 | Cinema |
| 30 | Video normale, dirette |
| 60 | Molto fluido: videogiochi, scorrimento delle pagine |

Più fps vuol dire più fluidità, ma anche più dati da comprimere e trasmettere.

### Perché il video va compresso

Facciamo il conto per un video **non compresso** a 1080p e 30 fps:

- ogni pixel occupa circa 4 byte (rosso, verde, blu e trasparenza);
- un frame: 1920 × 1080 × 4 ≈ **8,3 MB**;
- un secondo, cioè 30 frame: ≈ **250 MB al secondo**, circa **2 gigabit al secondo**.

Nessuna rete Wi-Fi di casa regge un flusso del genere in modo stabile. Per questo serve la compressione.

### Bitrate

Il **bitrate** è quanti dati al secondo occupa il video **dopo** la compressione. Si misura in **megabit al secondo (Mbit/s)**.

> **Analogia:** il diametro del tubo dell'acqua. Un tubo largo fa passare più acqua (più qualità), ma serve un impianto (una rete) che lo regga.

Attenzione ai due "mega": **8 bit = 1 byte**, quindi 8 Mbit/s = 1 MB/s.

Per una diretta a 1080p e 30 fps in H.264 si usano tipicamente alcuni Mbit/s: un valore in quella zona è un buon punto di partenza, poi lo misureremo noi nella Fase 5. Rispetto ai 2 gigabit del video grezzo, la compressione riduce i dati di centinaia di volte.

### Codec e H.264

Un **codec** (da *co*difica-*dec*odifica) è il metodo per comprimere e poi ricostruire il video. **H.264** (detto anche AVC) è il codec più diffuso al mondo: lo capiscono tutti i telefoni, i PC, i browser e OBS.

- **Codifica (encoding):** sull'iPhone, il video grezzo diventa H.264. Lo fa un circuito dedicato del chip (codifica *hardware*, tramite VideoToolbox), che è veloce e consuma poca batteria.
- **Decodifica (decoding):** sul PC, l'H.264 torna a essere immagini da mostrare.

Come fa H.264 a comprimere così tanto? Soprattutto con un'idea: **tra un frame e il successivo cambia poco**.

### Keyframe e frame "differenza"

> **Analogia:** invece di mandare ogni volta una foto intera, mandi una foto completa e poi solo dei bigliettini: "uguale a prima, ma la freccia del mouse si è spostata a destra".

- **Keyframe (I-frame):** una foto completa, che si capisce da sola. Pesa molto.
- **P-frame:** descrive solo le differenze rispetto al frame precedente. Pesa poco.
- **B-frame:** usa anche frame *futuri* come riferimento. Comprime ancora di più, ma obbliga ad aspettare i frame successivi, quindi aggiunge ritardo. **Per la diretta li evitiamo.**

La sequenza tipica è: `I P P P P P ... I P P P P P ...`

La distanza tra due keyframe si chiama **GOP** (*Group of Pictures*).

**Perché i keyframe sono importanti per noi:**

1. **Per partire.** Se il PC si collega a trasmissione già iniziata, non può mostrare niente finché non arriva un keyframe: i bigliettini con le differenze, senza la foto di partenza, non servono a nulla.
2. **Per riprendersi dagli errori.** Se un pezzo di dati va perso, l'immagine resta "sporca" (blocchi, colori sbagliati) fino al keyframe successivo.

Keyframe frequenti vogliono dire recupero più rapido, ma bitrate più alto. È un compromesso che regoleremo nella Fase 5.

### Latenza

La **latenza** è il ritardo tra quando succede una cosa sull'iPhone e quando la vedi sul PC.

> **Analogia:** una partita in TV dove senti l'urlo del vicino per il gol prima di vederlo, perché il suo segnale arriva prima del tuo.

Ogni passaggio aggiunge un po' di ritardo: cattura, compressione, rete, decompressione, visualizzazione. Il nostro obiettivo è tenerla bassa, ma la **misuriamo**, non la promettiamo.

---

## Parte 2 · La rete

### Indirizzo IP e porta

- L'**indirizzo IP** identifica un dispositivo sulla rete, per esempio `192.168.1.20`.
- La **porta** identifica un programma su quel dispositivo, ed è un numero da 1 a 65535, per esempio `5000`.

> **Analogia:** l'indirizzo IP è l'indirizzo di un palazzo, la porta è il numero dell'interno. La lettera arriva al palazzo giusto e poi all'appartamento giusto.

L'indirizzo speciale **`127.0.0.1`** (detto **localhost**) vuol dire "questo stesso computer". Lo useremo nella Fase 0 e nella Fase 1 per far parlare due programmi sullo stesso PC.

### TCP

**TCP** è un protocollo **affidabile**: garantisce che tutto arrivi, nell'ordine giusto, senza buchi.

> **Analogia:** una telefonata. Prima si stabilisce la linea (la **connessione**); se l'altro non capisce una parola, la ripeti; le parole arrivano nell'ordine in cui le dici.

- ✅ **Pro:** niente dati persi, ordine garantito, semplice da usare.
- ❌ **Contro:** se un pacchetto si perde, tutto ciò che viene dopo **aspetta** che venga rispedito. Nel video in diretta questo diventa un piccolo blocco, seguito da un accumulo di ritardo.

### UDP

**UDP** è un protocollo **senza garanzie**: manda i pacchetti e basta.

> **Analogia:** le cartoline. Le spedisci e non sai se arrivano; possono arrivare in disordine o perdersi, e nessuno le rispedisce.

- ✅ **Pro:** nessuna attesa e nessuna connessione da stabilire, quindi latenza minima.
- ❌ **Contro:** perdite e disordine vanno gestiti da noi. Nel video, una perdita produce un'immagine sporca fino al keyframe successivo.

### Quale usiamo

Come deciso nel piano: **prima TCP**, perché è semplice e fa funzionare tutto subito. Ottimizzeremo la latenza solo dopo, misurando. Sulla rete di casa le perdite sono rare, quindi TCP è un ottimo punto di partenza.

| | TCP | UDP |
| --- | --- | --- |
| Arriva tutto? | Sì | Non garantito |
| In ordine? | Sì | Non garantito |
| Connessione | Sì (come una telefonata) | No (come le cartoline) |
| Latenza | Può crescere se si perdono dati | Minima |
| Messaggi separati? | **No**, è un flusso continuo | Sì, ogni pacchetto è a sé |

### TCP è un flusso, non una sequenza di messaggi

È un dettaglio fondamentale per il nostro programma.

Con TCP, se mandi "Ciao" e poi "Come stai?", chi riceve può leggere `CiaoCome stai?` tutto attaccato, oppure `Cia` e poi `oCome stai?`. TCP garantisce l'**ordine dei byte**, non dove finisce un messaggio.

> **Analogia:** un nastro trasportatore senza separatori. Gli oggetti arrivano in ordine, ma se non metti dei divisori non sai dove finisce un pacco e comincia il successivo.

**Soluzione: il prefisso di lunghezza.** Prima di ogni messaggio mandiamo **4 byte che dicono quanto è lungo**. Chi riceve legge i 4 byte, scopre per esempio "seguono 11 byte", legge esattamente 11 byte, e ricomincia.

```
[ 00 00 00 04 ][ C  i  a  o ][ 00 00 00 0A ][ C  o  m  e     s  t  a  i  ? ]
   lunghezza 4    messaggio      lunghezza 10           messaggio
```

I 4 byte della lunghezza si scrivono in **big-endian** (il byte più importante per primo), che è la convenzione delle reti, detta anche "network byte order".

Questa stessa tecnica la useremo nella Fase 1 per i frame H.264: al posto delle frasi, sul nastro viaggeranno i frame compressi.

---

## Riepilogo in una riga

L'iPhone cattura i **frame**, li comprime in **H.264** (un **keyframe** ogni tanto, poi solo differenze) a un certo **bitrate** e **fps**, e li manda al PC via **TCP**, ognuno con il suo **prefisso di lunghezza**. Il PC li decomprime e li mostra, con la **latenza** più bassa possibile.
