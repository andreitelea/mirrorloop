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
/// Vince sempre l'ultima immagine: se la finestra non ha ancora mostrato quella precedente,
/// la nuova la sostituisce invece di mettersi in fila. Così il ritardo non si accumula.
/// </summary>
public sealed class Riproduzione(CodaFrame coda)
{
    private readonly ConcurrentBag<byte[]> _bufferLiberi = [];
    private ImmaginePronta? _pronta;
    private long _nonMostrate;
    private long _errori;

    /// <summary>Immagini sostituite da una più recente prima di essere mostrate.</summary>
    public long NonMostrate => Interlocked.Read(ref _nonMostrate);

    /// <summary>Frame che il decompressore non è riuscito a decomprimere.</summary>
    public long Errori => Interlocked.Read(ref _errori);

    public Thread Avvia(CancellationToken stop)
    {
        var thread = new Thread(() => Esegui(stop)) { IsBackground = true, Name = "Riproduzione" };
        thread.Start();
        return thread;
    }

    /// <summary>Per la finestra: prende l'immagine più recente, se ce n'è una nuova.</summary>
    public ImmaginePronta? PrendiUltima() => Interlocked.Exchange(ref _pronta, null);

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
                if (!coda.ProvaPrendi(TimeSpan.FromMilliseconds(200), out FrameVideo? frame) || frame is null)
                    continue;

                // Nuova connessione (o ripartenza dopo uno scarto): decompressore nuovo, stato pulito.
                if (coda.Generazione != generazione || decompressore is null)
                {
                    decompressore?.Dispose();
                    decompressore = new DecompressoreH264();
                    generazione = coda.Generazione;
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
                    coda.Ricomincia();
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

            var immagine = new ImmaginePronta(bgra, d.Larghezza, d.Altezza, d.Tempo, msDecompressione, msConversione);
            ImmaginePronta? scavalcata = Interlocked.Exchange(ref _pronta, immagine);
            if (scavalcata is not null)
            {
                Interlocked.Increment(ref _nonMostrate);
                _bufferLiberi.Add(scavalcata.Bgra);
            }
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
