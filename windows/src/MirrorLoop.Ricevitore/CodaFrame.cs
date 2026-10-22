using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Piccola coda tra chi riceve dalla rete e chi decomprime.
///
/// Regola d'oro: il ritardo non deve accumularsi. Se chi decomprime resta indietro e la coda
/// si riempie, buttiamo tutto e ripartiamo dal prossimo keyframe. Non si possono buttare frame
/// qualsiasi: un frame "differenza" senza il suo predecessore produce un'immagine rovinata.
///
/// "Piena" si misura in tempo (istanti di cattura tra il frame più vecchio in coda e quello che
/// arriva), non in numero di frame: lo stesso limite vale a 30 e a 60 fps. Con un limite di 8 frame,
/// a 60 fps bastava la raffica che segue una normale pausa del Wi-Fi da 100 ms per svuotare tutto e
/// fermare l'immagine fino al keyframe (prova del 22/10/2026: 112 frame buttati in 2 minuti).
/// </summary>
/// <param name="durataMassima">Predefinito 500 ms, come la soglia dei dati in attesa sull'iPhone.</param>
public sealed class CodaFrame(TimeSpan? durataMassima = null)
{
    private readonly long _microsecondiMassimi = (long)(durataMassima ?? TimeSpan.FromMilliseconds(500)).TotalMicroseconds;
    private readonly Queue<FrameVideo> _coda = new();
    private readonly object _blocco = new();
    private bool _attendoKeyframe = true;

    /// <summary>Frame buttati: in attesa del keyframe o per coda piena.</summary>
    public long Scartati { get; private set; }

    /// <summary>
    /// Cambia ogni volta che il flusso riparte da capo (nuova connessione o scarto per coda piena):
    /// chi decomprime lo usa per sapere che deve ripartire con un decompressore pulito.
    /// </summary>
    public long Generazione { get { lock (_blocco) return _generazione; } }
    private long _generazione;

    /// <summary>Nuova trasmissione: si svuota la coda e si aspetta un keyframe.</summary>
    public void Ricomincia()
    {
        lock (_blocco)
        {
            _coda.Clear();
            _attendoKeyframe = true;
            _generazione++;
        }
    }

    public void Aggiungi(FrameVideo frame)
    {
        lock (_blocco)
        {
            if (_coda.TryPeek(out FrameVideo? piuVecchio)
                && frame.IstanteCatturaMicrosecondi - piuVecchio.IstanteCatturaMicrosecondi > _microsecondiMassimi)
            {
                Scartati += _coda.Count;
                _coda.Clear();
                _attendoKeyframe = true;
                _generazione++;
            }

            if (_attendoKeyframe && !frame.Keyframe)
            {
                Scartati++;
                return;
            }

            _attendoKeyframe = false;
            _coda.Enqueue(frame);
            Monitor.Pulse(_blocco);
        }
    }

    /// <summary>Prende il prossimo frame, aspettando al massimo <paramref name="attesa"/>.</summary>
    public bool ProvaPrendi(TimeSpan attesa, out FrameVideo? frame)
    {
        lock (_blocco)
        {
            if (_coda.Count == 0)
                Monitor.Wait(_blocco, attesa);

            return _coda.TryDequeue(out frame);
        }
    }
}
