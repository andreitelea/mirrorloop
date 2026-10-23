using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Quale rotazione usare per mostrare il video: quella che manda l'iPhone (dal sensore di
/// movimento) oppure una scelta a mano. Il sensore sbaglia quando l'app in primo piano non ruota
/// (Home, app solo verticali, blocco della rotazione): lì si corregge con un tasto.
///
/// La scelta manuale non si salva: a ogni avvio si riparte dall'automatica, così una correzione
/// fatta per un'altra app non resta attiva per sbaglio.
///
/// Thread-safe: la rotazione dell'iPhone arriva dal thread che sceglie le immagini, i tasti dalla finestra.
/// </summary>
public sealed class SceltaRotazione
{
    private readonly object _blocco = new();
    private RotazioneVideo? _manuale;
    private RotazioneVideo _automatica = RotazioneVideo.Nessuna;

    /// <summary>L'ultima rotazione arrivata dall'iPhone.</summary>
    public RotazioneVideo Automatica
    {
        get
        {
            lock (_blocco)
                return _automatica;
        }
    }

    public bool Manuale
    {
        get
        {
            lock (_blocco)
                return _manuale is not null;
        }
    }

    /// <summary>Quella da applicare all'immagine.</summary>
    public RotazioneVideo Effettiva
    {
        get
        {
            lock (_blocco)
                return _manuale ?? _automatica;
        }
    }

    /// <summary>Registra la rotazione arrivata dall'iPhone e restituisce quella da applicare.</summary>
    public RotazioneVideo DaIPhone(RotazioneVideo rotazione)
    {
        lock (_blocco)
        {
            _automatica = rotazione;
            return _manuale ?? _automatica;
        }
    }

    /// <summary>Gira di 90° in senso orario rispetto a quanto si vede adesso; da qui in poi decide la mano.</summary>
    public void RuotaManuale()
    {
        lock (_blocco)
            _manuale = (RotazioneVideo)(((int)(_manuale ?? _automatica) + 1) % 4);
    }

    public void TornaAutomatica()
    {
        lock (_blocco)
            _manuale = null;
    }
}
