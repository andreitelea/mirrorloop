using System.Buffers.Binary;

namespace MirrorLoop.Protocollo;

/// <summary>
/// Formato dei messaggi tra mittente e ricevitore. Descrizione completa in docs/protocollo.md.
///
/// Ogni messaggio:  [lunghezza: 4 byte][tipo: 1 byte][contenuto]
/// dove "lunghezza" conta tipo + contenuto. Tutti i numeri sono big-endian.
/// </summary>
public static class Protocollo
{
    /// <summary>
    /// Limite per un singolo messaggio. Un keyframe 1080p di buona qualità sta sotto il megabyte:
    /// 8 MB lasciano ampio margine e proteggono da lunghezze corrotte.
    /// </summary>
    public const int LunghezzaMassima = 8 * 1024 * 1024;

    private const byte TipoFrameVideo = 1;
    private const byte TipoAudio = 2;
    private const byte TipoPing = 3;
    private const byte TipoPong = 4;

    private const byte FlagKeyframe = 0b0000_0001;

    // tipo (1) + flag (1) + istante di cattura (8)
    private const int IntestazioneFrame = 1 + 1 + 8;

    // tipo (1) + codifica (1) + canali (1) + frequenza (4) + istante di cattura (8)
    private const int IntestazioneAudio = 1 + 1 + 1 + 4 + 8;

    // tipo (1) + numero (4) + T1 (8)
    private const int LunghezzaPing = 1 + 4 + 8;

    // tipo (1) + numero (4) + T1 (8) + T2 (8) + T3 (8)
    private const int LunghezzaPong = 1 + 4 + 8 + 8 + 8;

    public static async Task InviaFrameAsync(Stream flusso, FrameVideo frame, CancellationToken ct = default)
    {
        if (frame.DatiH264.IsEmpty)
            throw new ArgumentException("Il frame non contiene dati H.264.", nameof(frame));

        int lunghezza = IntestazioneFrame + frame.DatiH264.Length;
        if (lunghezza > LunghezzaMassima)
            throw new ArgumentException($"Frame troppo grande ({frame.DatiH264.Length} byte).", nameof(frame));

        // Un'unica scrittura per messaggio: con NoDelay attivo, scritture separate
        // diventerebbero pacchetti TCP separati.
        byte[] pacchetto = new byte[4 + lunghezza];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, lunghezza);
        s[4] = TipoFrameVideo;
        s[5] = frame.Keyframe ? FlagKeyframe : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(s[6..], frame.IstanteCatturaMicrosecondi);
        frame.DatiH264.Span.CopyTo(s[(4 + IntestazioneFrame)..]);

        await flusso.WriteAsync(pacchetto, ct);
    }

    public static async Task InviaAudioAsync(Stream flusso, PacchettoAudio audio, CancellationToken ct = default)
    {
        if (audio.Dati.IsEmpty)
            throw new ArgumentException("Il pacchetto non contiene campioni.", nameof(audio));
        if (audio.Canali is < 1 or > 255 || audio.Frequenza < 1)
            throw new ArgumentException("Canali o frequenza non validi.", nameof(audio));

        int lunghezza = IntestazioneAudio + audio.Dati.Length;
        if (lunghezza > LunghezzaMassima)
            throw new ArgumentException($"Pacchetto audio troppo grande ({audio.Dati.Length} byte).", nameof(audio));

        byte[] pacchetto = new byte[4 + lunghezza];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, lunghezza);
        s[4] = TipoAudio;
        s[5] = (byte)audio.Codifica;
        s[6] = (byte)audio.Canali;
        BinaryPrimitives.WriteInt32BigEndian(s[7..], audio.Frequenza);
        BinaryPrimitives.WriteInt64BigEndian(s[11..], audio.IstanteCatturaMicrosecondi);
        audio.Dati.Span.CopyTo(s[(4 + IntestazioneAudio)..]);

        await flusso.WriteAsync(pacchetto, ct);
    }

    public static async Task InviaPingAsync(Stream flusso, Ping ping, CancellationToken ct = default)
    {
        byte[] pacchetto = new byte[4 + LunghezzaPing];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, LunghezzaPing);
        s[4] = TipoPing;
        BinaryPrimitives.WriteInt32BigEndian(s[5..], ping.Numero);
        BinaryPrimitives.WriteInt64BigEndian(s[9..], ping.T1);
        await flusso.WriteAsync(pacchetto, ct);
    }

    public static async Task InviaPongAsync(Stream flusso, Pong pong, CancellationToken ct = default)
    {
        byte[] pacchetto = new byte[4 + LunghezzaPong];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, LunghezzaPong);
        s[4] = TipoPong;
        BinaryPrimitives.WriteInt32BigEndian(s[5..], pong.Numero);
        BinaryPrimitives.WriteInt64BigEndian(s[9..], pong.T1);
        BinaryPrimitives.WriteInt64BigEndian(s[17..], pong.T2);
        BinaryPrimitives.WriteInt64BigEndian(s[25..], pong.T3);
        await flusso.WriteAsync(pacchetto, ct);
    }

    /// <summary>
    /// Legge il prossimo frame video, saltando tutti gli altri messaggi. Restituisce null se
    /// l'altro ha chiuso la connessione in modo pulito, tra un messaggio e l'altro.
    /// </summary>
    /// <exception cref="EndOfStreamException">La connessione si è interrotta a metà di un messaggio.</exception>
    /// <exception cref="InvalidDataException">Il messaggio non rispetta il formato.</exception>
    public static async Task<FrameVideo?> RiceviFrameAsync(Stream flusso, CancellationToken ct = default)
    {
        while (true)
        {
            switch (await RiceviMessaggioAsync(flusso, ct))
            {
                case null:
                    return null;
                case FrameVideo frame:
                    return frame;
            }
        }
    }

    /// <summary>
    /// Legge il prossimo messaggio conosciuto (video, audio, ping o pong). Restituisce null se l'altro ha
    /// chiuso la connessione in modo pulito, tra un messaggio e l'altro.
    /// </summary>
    /// <exception cref="EndOfStreamException">La connessione si è interrotta a metà di un messaggio.</exception>
    /// <exception cref="InvalidDataException">Il messaggio non rispetta il formato.</exception>
    public static async Task<Messaggio?> RiceviMessaggioAsync(Stream flusso, CancellationToken ct = default)
    {
        byte[] prefisso = new byte[4];

        while (true)
        {
            int letti = await flusso.ReadAtLeastAsync(prefisso, 4, throwOnEndOfStream: false, ct);
            if (letti == 0)
                return null;
            if (letti < 4)
                throw new EndOfStreamException("Connessione interrotta a metà di un messaggio.");

            int lunghezza = BinaryPrimitives.ReadInt32BigEndian(prefisso);
            if (lunghezza < 1 || lunghezza > LunghezzaMassima)
                throw new InvalidDataException($"Lunghezza del messaggio non valida: {lunghezza} byte.");

            byte[] messaggio = new byte[lunghezza];
            await flusso.ReadExactlyAsync(messaggio, ct);

            switch (messaggio[0])
            {
                case TipoFrameVideo:
                    return LeggiFrame(messaggio);
                case TipoAudio:
                    return LeggiAudio(messaggio);
                case TipoPing:
                    return LeggiPing(messaggio);
                case TipoPong:
                    return LeggiPong(messaggio);
                default:
                    // Tipi sconosciuti si saltano invece di dare errore: così un mittente più nuovo
                    // può aggiungere messaggi senza rompere un ricevitore più vecchio.
                    continue;
            }
        }
    }

    private static FrameVideo LeggiFrame(byte[] messaggio)
    {
        if (messaggio.Length <= IntestazioneFrame)
            throw new InvalidDataException("Frame video senza dati H.264.");

        bool keyframe = (messaggio[1] & FlagKeyframe) != 0;
        long istante = BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(2));
        var dati = new ReadOnlyMemory<byte>(messaggio, IntestazioneFrame, messaggio.Length - IntestazioneFrame);
        return new FrameVideo(keyframe, istante, dati);
    }

    private static PacchettoAudio LeggiAudio(byte[] messaggio)
    {
        if (messaggio.Length <= IntestazioneAudio)
            throw new InvalidDataException("Pacchetto audio senza campioni.");

        var codifica = (CodificaAudio)messaggio[1];
        int canali = messaggio[2];
        int frequenza = BinaryPrimitives.ReadInt32BigEndian(messaggio.AsSpan(3));
        long istante = BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(7));
        if (canali < 1 || frequenza < 1)
            throw new InvalidDataException($"Pacchetto audio con canali ({canali}) o frequenza ({frequenza}) non validi.");

        var dati = new ReadOnlyMemory<byte>(messaggio, IntestazioneAudio, messaggio.Length - IntestazioneAudio);
        // Con il PCM a 16 bit ogni "istante" occupa 2 byte per canale: un resto vorrebbe dire
        // campioni spezzati, quindi dati corrotti.
        if (codifica == CodificaAudio.Pcm16 && dati.Length % (2 * canali) != 0)
            throw new InvalidDataException($"Pacchetto PCM di {dati.Length} byte non divisibile in campioni da {2 * canali} byte.");

        return new PacchettoAudio(codifica, canali, frequenza, istante, dati);
    }

    private static Ping LeggiPing(byte[] messaggio)
    {
        if (messaggio.Length != LunghezzaPing)
            throw new InvalidDataException($"Ping di {messaggio.Length} byte invece di {LunghezzaPing}.");
        return new Ping(
            BinaryPrimitives.ReadInt32BigEndian(messaggio.AsSpan(1)),
            BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(5)));
    }

    private static Pong LeggiPong(byte[] messaggio)
    {
        if (messaggio.Length != LunghezzaPong)
            throw new InvalidDataException($"Pong di {messaggio.Length} byte invece di {LunghezzaPong}.");
        return new Pong(
            BinaryPrimitives.ReadInt32BigEndian(messaggio.AsSpan(1)),
            BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(5)),
            BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(13)),
            BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(21)));
    }

    /// <summary>L'istante attuale nel formato usato da <see cref="FrameVideo.IstanteCatturaMicrosecondi"/>.</summary>
    public static long AdessoMicrosecondi() =>
        (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;
}
