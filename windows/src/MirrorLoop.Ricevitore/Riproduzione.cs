using System.Collections.Concurrent;
using System.Diagnostics;
using MirrorLoop.MediaFoundation;
using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>Un'immagine pronta da mostrare, con le misure raccolte lungo la strada.</summary>
public sealed record ImmaginePronta(byte[] Bgra, int Larghezza, int Altezza, long IstanteCatturaMicrosecondi, double MsDecompressione, double MsConversione);

/// <summary>
/// Prende i frame dalla coda, li decomprime e li converte in immagini da mostrare.
/// Lavora su un thread tutto suo: Media Foundation va usato sempre dallo stesso thread.
///
/// Le immagini pronte aspettano in una <see cref="CodaImmagini"/>, che decide quale mostrare
/// in base all'audio (sincronia) o, senza audio, consegna sempre la più recente.
/// </summary>
public sealed class Riproduzione
{
    private readonly CodaFrame _coda;
    private readonly ConcurrentBag<byte[]> _bufferLiberi = [];
    private readonly CodaImmagini _immagini;
    private long _errori;

    public Riproduzione(CodaFrame coda)
    {
        _coda = coda;
        _immagini = new CodaImmagini(immagine => _bufferLiberi.Add(immagine.Bgra));
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

    /// <summary>Per la finestra: restituisce il buffer di un'immagine già mostrata, così si riusa.</summary>
    public void Restituisci(ImmaginePronta immagine) => _bufferLiberi.Add(immagine.Bgra);

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

        foreach (FrameDecompresso d in decompressi)
        {
            cronometro.Restart();
            byte[] bgra = PrendiBuffer(d.Larghezza * d.Altezza * 4);
            ConversioneColori.Nv12ABgra(d.Nv12, d.Larghezza, d.Altezza, d.Passo, bgra, d.Larghezza * 4);
            double msConversione = cronometro.Elapsed.TotalMilliseconds;

            _immagini.Aggiungi(new ImmaginePronta(bgra, d.Larghezza, d.Altezza, d.Tempo, msDecompressione, msConversione));
        }
    }

    private byte[] PrendiBuffer(int dimensione)
    {
        // I buffer di dimensione diversa (risoluzione cambiata) si lasciano al garbage collector.
        while (_bufferLiberi.TryTake(out byte[]? buffer))
        {
            if (buffer.Length == dimensione)
                return buffer;
        }
        return new byte[dimensione];
    }
}
