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

    private const byte FlagKeyframe = 0b0000_0001;

    // tipo (1) + flag (1) + istante di cattura (8)
    private const int IntestazioneFrame = 1 + 1 + 8;

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

    /// <summary>
    /// Legge il prossimo frame video. Restituisce null se l'altro ha chiuso la connessione
    /// in modo pulito, tra un messaggio e l'altro.
    /// </summary>
    /// <exception cref="EndOfStreamException">La connessione si è interrotta a metà di un messaggio.</exception>
    /// <exception cref="InvalidDataException">Il messaggio non rispetta il formato.</exception>
    public static async Task<FrameVideo?> RiceviFrameAsync(Stream flusso, CancellationToken ct = default)
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

            // Tipi sconosciuti si saltano invece di dare errore: così un mittente più nuovo
            // può aggiungere messaggi (es. l'audio nella Fase 4) senza rompere un ricevitore più vecchio.
            if (messaggio[0] != TipoFrameVideo)
                continue;

            if (lunghezza <= IntestazioneFrame)
                throw new InvalidDataException("Frame video senza dati H.264.");

            bool keyframe = (messaggio[1] & FlagKeyframe) != 0;
            long istante = BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(2));
            var dati = new ReadOnlyMemory<byte>(messaggio, IntestazioneFrame, lunghezza - IntestazioneFrame);

            return new FrameVideo(keyframe, istante, dati);
        }
    }

    /// <summary>L'istante attuale nel formato usato da <see cref="FrameVideo.IstanteCatturaMicrosecondi"/>.</summary>
    public static long AdessoMicrosecondi() =>
        (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;
}
