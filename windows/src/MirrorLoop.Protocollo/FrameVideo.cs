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
public sealed record FrameVideo(bool Keyframe, long IstanteCatturaMicrosecondi, ReadOnlyMemory<byte> DatiH264) : Messaggio;
