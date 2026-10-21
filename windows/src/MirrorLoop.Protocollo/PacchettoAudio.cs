namespace MirrorLoop.Protocollo;

/// <summary>Come sono codificati i campioni di un <see cref="PacchettoAudio"/>.</summary>
public enum CodificaAudio : byte
{
    /// <summary>
    /// Non compresso: interi con segno a 16 bit little-endian (come nei file WAV),
    /// canali intercalati (sinistro, destro, sinistro, destro…).
    /// </summary>
    Pcm16 = 1,
}

/// <summary>
/// Un pezzo di audio catturato, pronto da spedire o appena ricevuto.
/// </summary>
/// <param name="Codifica">Formato dei campioni. Un ricevitore ignora le codifiche che non conosce.</param>
/// <param name="Canali">1 = mono, 2 = stereo.</param>
/// <param name="Frequenza">Campioni al secondo per canale (es. 48 000).</param>
/// <param name="IstanteCatturaMicrosecondi">
/// Quando è stato catturato il primo campione, con lo stesso orologio dei frame video:
/// è ciò che permette di sincronizzare audio e video.
/// </param>
/// <param name="Dati">I campioni, nel formato indicato da <paramref name="Codifica"/>.</param>
public sealed record PacchettoAudio(
    CodificaAudio Codifica,
    int Canali,
    int Frequenza,
    long IstanteCatturaMicrosecondi,
    ReadOnlyMemory<byte> Dati) : Messaggio
{
    /// <summary>Durata del pacchetto, se la codifica è nota; altrimenti zero.</summary>
    public TimeSpan Durata => Codifica == CodificaAudio.Pcm16 && Canali > 0 && Frequenza > 0
        ? TimeSpan.FromSeconds((double)Dati.Length / (2 * Canali) / Frequenza)
        : TimeSpan.Zero;
}
