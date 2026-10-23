namespace MirrorLoop.Ricevitore;

/// <summary>
/// Immagini già decompresse in attesa di essere mostrate, in ordine di istante di cattura.
///
/// Con l'audio che suona, "l'audio comanda": si mostra l'immagine più recente il cui istante di
/// cattura è già stato raggiunto dall'audio, così le immagini aspettano quanto la riserva audio
/// e restano allineate al suono. Senza audio si mostra subito la più recente, come prima.
///
/// Thread-safe: <see cref="Aggiungi"/> dal thread di decompressione, <see cref="Prendi"/> dalla finestra.
/// </summary>
public sealed class CodaImmagini(Action<ImmaginePronta> restituisci)
{
    /// <summary>Oltre questo numero (circa 1,5 s a 30 fps) si buttano le più vecchie.</summary>
    public const int Massimo = 45;

    /// <summary>
    /// Se l'audio è indietro di più di così rispetto all'immagine più recente, l'orologio audio
    /// non è credibile (audio fermo, appena ripartito…) e si torna a mostrare subito il video.
    /// </summary>
    public static readonly TimeSpan RitardoMassimoCredibile = TimeSpan.FromSeconds(1);

    private readonly object _blocco = new();
    private readonly List<ImmaginePronta> _immagini = [];
    private long _nonMostrate;

    /// <summary>Immagini buttate senza essere mostrate (sostituite da una più adatta o in eccesso).</summary>
    public long NonMostrate => Interlocked.Read(ref _nonMostrate);

    public void Aggiungi(ImmaginePronta immagine)
    {
        lock (_blocco)
        {
            _immagini.Add(immagine);
            if (_immagini.Count > Massimo)
                Butta(1);
        }
    }

    /// <summary>
    /// L'immagine da mostrare adesso, o null se non ce n'è una nuova.
    /// </summary>
    /// <param name="istanteAudio">L'istante di cattura che si sta ascoltando, o null senza audio.</param>
    public ImmaginePronta? Prendi(long? istanteAudio)
    {
        lock (_blocco)
        {
            if (_immagini.Count == 0)
                return null;

            long piuRecente = _immagini[^1].IstanteCatturaMicrosecondi;
            bool audioCredibile = istanteAudio is long audio
                && piuRecente - audio <= (long)RitardoMassimoCredibile.TotalMicroseconds;

            int scelta;
            if (!audioCredibile)
            {
                scelta = _immagini.Count - 1;
            }
            else
            {
                // La più recente già "raggiunta" dall'audio; se nessuna lo è ancora, si aspetta.
                scelta = _immagini.FindLastIndex(i => i.IstanteCatturaMicrosecondi <= istanteAudio!.Value);
                if (scelta < 0)
                    return null;
            }

            ImmaginePronta presa = _immagini[scelta];
            Butta(scelta);
            _immagini.RemoveAt(0);
            return presa;
        }
    }

    /// <summary>Istante di cattura della prossima immagine in attesa, o null se la coda è vuota.</summary>
    public long? ProssimoIstante
    {
        get
        {
            lock (_blocco)
                return _immagini.Count > 0 ? _immagini[0].IstanteCatturaMicrosecondi : null;
        }
    }

    public int Conteggio
    {
        get
        {
            lock (_blocco)
                return _immagini.Count;
        }
    }

    /// <summary>Butta le prime <paramref name="quante"/> immagini restituendone i buffer.</summary>
    private void Butta(int quante)
    {
        for (int i = 0; i < quante; i++)
        {
            restituisci(_immagini[i]);
            Interlocked.Increment(ref _nonMostrate);
        }
        _immagini.RemoveRange(0, quante);
    }
}
