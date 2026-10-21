namespace MirrorLoop.Protocollo;

/// <summary>Un messaggio ricevuto: <see cref="FrameVideo"/> oppure <see cref="PacchettoAudio"/>.</summary>
public abstract record Messaggio;

/// <summary>
/// Un frame video compresso, pronto da spedire o appena ricevuto.
/// </summary>
/// <param name="Keyframe">
/// True se il frame si decodifica da solo (I-frame). Chi si collega a trasmissione iniziata
/// deve scartare tutto finché non ne arriva uno.
/// </param>
/// <param name="IstanteCatturaMicrosecondi">
/// Quando il frame è stato catturato, in microsecondi dal 1/1/1970 UTC.
/// Serve a misurare la latenza: ha senso solo se mittente e ricevitore hanno orologi allineati
/// (vero sullo stesso PC; tra iPhone e PC andrà gestito a parte).
/// </param>
/// <param name="DatiH264">Il frame compresso in formato H.264 Annex B (con i codici di inizio 00 00 00 01).</param>
/// <param name="Rotazione">
/// Di quanto girare l'immagine, in senso orario, per mostrarla dritta. L'iPhone cattura sempre
/// nel verso del pannello: con il telefono in orizzontale il contenuto arriva coricato.
/// </param>
public sealed record FrameVideo(bool Keyframe, long IstanteCatturaMicrosecondi, ReadOnlyMemory<byte> DatiH264,
    RotazioneVideo Rotazione = RotazioneVideo.Nessuna) : Messaggio;

/// <summary>Rotazione in senso orario, a passi di 90° (bit 1 e 2 del flag del frame video).</summary>
public enum RotazioneVideo : byte
{
    Nessuna = 0,
    Gradi90 = 1,
    Gradi180 = 2,
    Gradi270 = 3,
}
