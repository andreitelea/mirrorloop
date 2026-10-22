using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Quale rotazione usare per mostrare il video: quella che manda l'iPhone (dal sensore di
/// movimento) oppure una scelta a mano. Il sensore sbaglia quando l'app in primo piano non ruota
/// (Home, app solo verticali, blocco della rotazione): lì si corregge con un tasto.
///
/// La scelta manuale non si salva: a ogni avvio si riparte dall'automatica, così una correzione
/// fatta per un'altra app non resta attiva per sbaglio. Si usa solo dal thread della finestra.
/// </summary>
public sealed class SceltaRotazione
{
    private RotazioneVideo? _manuale;

    /// <summary>L'ultima rotazione arrivata dall'iPhone.</summary>
    public RotazioneVideo Automatica { get; private set; } = RotazioneVideo.Nessuna;

    public bool Manuale => _manuale is not null;

    /// <summary>Quella da applicare all'immagine.</summary>
    public RotazioneVideo Effettiva => _manuale ?? Automatica;

    public void DaIPhone(RotazioneVideo rotazione) => Automatica = rotazione;

    /// <summary>Gira di 90° in senso orario rispetto a quanto si vede adesso; da qui in poi decide la mano.</summary>
    public void RuotaManuale() => _manuale = (RotazioneVideo)(((int)Effettiva + 1) % 4);

    public void TornaAutomatica() => _manuale = null;
}
