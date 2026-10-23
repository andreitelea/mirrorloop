using System.Diagnostics;
using MirrorLoop.MediaFoundation;
using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Un'immagine decompressa, ancora in NV12 (il formato del decompressore): si converte solo se e
/// quando viene davvero mostrata.
/// </summary>
/// <param name="Nv12">Piano Y (altezza righe) seguito dal piano UV (altezza / 2 righe), righe lunghe <paramref name="Passo"/> byte.</param>
/// <param name="Rotazione">Di quanto girarla in senso orario per mostrarla dritta (la gira la finestra).</param>
public sealed record ImmaginePronta(byte[] Nv12, int Larghezza, int Altezza, int Passo, long IstanteCatturaMicrosecondi,
    double MsDecompressione, RotazioneVideo Rotazione = RotazioneVideo.Nessuna);

/// <summary>
/// Prende i frame dalla coda e li decomprime.
/// Lavora su un thread tutto suo: Media Foundation va usato sempre dallo stesso thread.
///
/// Le immagini decompresse aspettano in una <see cref="CodaImmagini"/>, che decide quale mostrare
/// in base all'audio (sincronia) o, senza audio, consegna sempre la più recente. Restano in NV12
/// (1,5 byte per pixel contro i 4 del BGRA della finestra): molte vengono saltate per la sincronia,
/// ed è inutile convertirle o occupare memoria per loro.
/// </summary>
public sealed class Riproduzione
{
    private readonly CodaFrame _coda;
    private readonly CodaImmagini _immagini;
    private long _errori;

    public Riproduzione(CodaFrame coda)
    {
        _coda = coda;
        // I buffer NV12 li crea il decompressore, uno per frame: non c'è niente da riusare.
        _immagini = new CodaImmagini(_ => { });
    }

    /// <summary>Immagini buttate senza essere mostrate.</summary>
    public long NonMostrate => _immagini.NonMostrate;

    /// <summary>Immagini decompresse in attesa del loro turno.</summary>
    public int InAttesa => _immagini.Conteggio;

    /// <summary>Frame che il decompressore non è riuscito a decomprimere.</summary>
    public long Errori => Interlocked.Read(ref _errori);

    public Thread Avvia(CancellationToken stop)
    {
        var thread = new Thread(() => Esegui(stop)) { IsBackground = true, Name = "Riproduzione" };
        thread.Start();
        return thread;
    }

    /// <summary>Per la finestra: l'immagine da mostrare adesso, se ce n'è una nuova.</summary>
    /// <param name="istanteAudio">L'istante di cattura che si sta ascoltando, o null senza audio.</param>
    public ImmaginePronta? Prendi(long? istanteAudio) => _immagini.Prendi(istanteAudio);

    private void Esegui(CancellationToken stop)
    {
        DecompressoreH264? decompressore = null;
        long generazione = -1;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!_coda.ProvaPrendi(TimeSpan.FromMilliseconds(200), out FrameVideo? frame) || frame is null)
                    continue;

                // Nuova connessione (o ripartenza dopo uno scarto): decompressore nuovo, stato pulito.
                if (_coda.Generazione != generazione || decompressore is null)
                {
                    decompressore?.Dispose();
                    decompressore = new DecompressoreH264();
                    generazione = _coda.Generazione;
                }

                try
                {
                    Elabora(decompressore, frame);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Dati rovinati: si butta il decompressore e si riparte dal prossimo keyframe.
                    Interlocked.Increment(ref _errori);
                    decompressore.Dispose();
                    decompressore = null;
                    _coda.Ricomincia();
                }
            }
        }
        finally
        {
            decompressore?.Dispose();
        }
    }

    private void Elabora(DecompressoreH264 decompressore, FrameVideo frame)
    {
        var cronometro = Stopwatch.StartNew();
        List<FrameDecompresso> decompressi = decompressore.Decomprimi(frame.DatiH264.Span, frame.IstanteCatturaMicrosecondi);
        double msDecompressione = cronometro.Elapsed.TotalMilliseconds;

        // Il decompressore è in bassa latenza (un frame entra, un frame esce): l'immagine che
        // esce è quella del frame appena entrato, e ne prende la rotazione.
        foreach (FrameDecompresso d in decompressi)
            _immagini.Aggiungi(new ImmaginePronta(d.Nv12, d.Larghezza, d.Altezza, d.Passo, d.Tempo, msDecompressione, frame.Rotazione));
    }
}
