namespace MirrorLoop.Protocollo;

/// <summary>Che cosa viaggia su una corsia QUIC.</summary>
public enum RuoloCorsia : byte
{
    /// <summary>Ping dal ricevitore e pong dal mittente: l'unica corsia su cui scrive anche il ricevitore.</summary>
    Controllo = 1,
    Video = 2,
    Audio = 3,
}

/// <summary>
/// Primo messaggio di ogni corsia QUIC (tipo 5). Il numero di trasmissione, scelto a caso dal
/// mittente a ogni tentativo di collegamento, permette al ricevitore di riunire corsie che
/// arrivano su connessioni QUIC separate.
/// </summary>
public sealed record AperturaCorsia(RuoloCorsia Ruolo, long NumeroTrasmissione) : Messaggio;
