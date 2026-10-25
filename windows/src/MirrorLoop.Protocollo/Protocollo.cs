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
    private const byte TipoAperturaCorsia = 5;
    private const byte TipoPresentazione = 6;
    private const byte TipoSfida = 7;
    private const byte TipoProva = 8;
    private const byte TipoEsito = 9;

    private const byte FlagKeyframe = 0b0000_0001;
    private const byte FlagRotazione = 0b0000_0110;
    private const int SpostamentoRotazione = 1;

    // tipo (1) + flag (1) + istante di cattura (8)
    private const int IntestazioneFrame = 1 + 1 + 8;

    // tipo (1) + codifica (1) + canali (1) + frequenza (4) + istante di cattura (8)
    private const int IntestazioneAudio = 1 + 1 + 1 + 4 + 8;

    // tipo (1) + numero (4) + T1 (8)
    private const int LunghezzaPing = 1 + 4 + 8;

    // tipo (1) + numero (4) + T1 (8) + T2 (8) + T3 (8)
    private const int LunghezzaPong = 1 + 4 + 8 + 8 + 8;

    // tipo (1) + ruolo (1) + numero di trasmissione (8)
    private const int LunghezzaAperturaCorsia = 1 + 1 + 8;

    // tipo (1) + chiave (65) + impegno (32), poi il nome
    private const int IntestazionePresentazione = 1 + Abbinamento.LunghezzaChiave + Abbinamento.LunghezzaHash;
    private const int LunghezzaMassimaNome = 64;

    // tipo (1) + numero del PC (32)
    private const int LunghezzaSfida = 1 + Abbinamento.LunghezzaNumero;

    // tipo (1) + numero dell'iPhone (32) + firma (64)
    private const int LunghezzaProva = 1 + Abbinamento.LunghezzaNumero + Abbinamento.LunghezzaFirma;

    // tipo (1) + esito (1)
    private const int LunghezzaEsito = 1 + 1;

    public static async Task InviaFrameAsync(Stream flusso, FrameVideo frame, CancellationToken ct = default)
    {
        if (frame.DatiH264.IsEmpty)
            throw new ArgumentException("Il frame non contiene dati H.264.", nameof(frame));

        int lunghezza = IntestazioneFrame + frame.DatiH264.Length;
        if (lunghezza > LunghezzaMassima)
            throw new ArgumentException($"Frame troppo grande ({frame.DatiH264.Length} byte).", nameof(frame));

        // Un'unica scrittura per messaggio: scritture separate possono diventare pacchetti separati.
        byte[] pacchetto = new byte[4 + lunghezza];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, lunghezza);
        s[4] = TipoFrameVideo;
        s[5] = (byte)((frame.Keyframe ? FlagKeyframe : 0) | (((int)frame.Rotazione << SpostamentoRotazione) & FlagRotazione));
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

    public static async Task InviaAperturaCorsiaAsync(Stream flusso, AperturaCorsia apertura, CancellationToken ct = default)
    {
        byte[] pacchetto = new byte[4 + LunghezzaAperturaCorsia];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, LunghezzaAperturaCorsia);
        s[4] = TipoAperturaCorsia;
        s[5] = (byte)apertura.Ruolo;
        BinaryPrimitives.WriteInt64BigEndian(s[6..], apertura.NumeroTrasmissione);
        await flusso.WriteAsync(pacchetto, ct);
    }

    public static async Task InviaPresentazioneAsync(Stream flusso, Presentazione presentazione, CancellationToken ct = default)
    {
        ControllaLunghezza(presentazione.Chiave, Abbinamento.LunghezzaChiave, "chiave");
        ControllaLunghezza(presentazione.Impegno, Abbinamento.LunghezzaHash, "impegno");
        byte[] nome = System.Text.Encoding.UTF8.GetBytes(presentazione.Nome);
        if (nome.Length > LunghezzaMassimaNome)
            throw new ArgumentException($"Nome troppo lungo ({nome.Length} byte, massimo {LunghezzaMassimaNome}).", nameof(presentazione));

        int lunghezza = IntestazionePresentazione + nome.Length;
        byte[] pacchetto = new byte[4 + lunghezza];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, lunghezza);
        s[4] = TipoPresentazione;
        presentazione.Chiave.CopyTo(s[5..]);
        presentazione.Impegno.CopyTo(s[(5 + Abbinamento.LunghezzaChiave)..]);
        nome.CopyTo(s[(4 + IntestazionePresentazione)..]);
        await flusso.WriteAsync(pacchetto, ct);
    }

    public static async Task InviaSfidaAsync(Stream flusso, Sfida sfida, CancellationToken ct = default)
    {
        ControllaLunghezza(sfida.NumeroPc, Abbinamento.LunghezzaNumero, "numero del PC");
        byte[] pacchetto = new byte[4 + LunghezzaSfida];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, LunghezzaSfida);
        s[4] = TipoSfida;
        sfida.NumeroPc.CopyTo(s[5..]);
        await flusso.WriteAsync(pacchetto, ct);
    }

    public static async Task InviaProvaAsync(Stream flusso, Prova prova, CancellationToken ct = default)
    {
        ControllaLunghezza(prova.NumeroIphone, Abbinamento.LunghezzaNumero, "numero dell'iPhone");
        ControllaLunghezza(prova.Firma, Abbinamento.LunghezzaFirma, "firma");
        byte[] pacchetto = new byte[4 + LunghezzaProva];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, LunghezzaProva);
        s[4] = TipoProva;
        prova.NumeroIphone.CopyTo(s[5..]);
        prova.Firma.CopyTo(s[(5 + Abbinamento.LunghezzaNumero)..]);
        await flusso.WriteAsync(pacchetto, ct);
    }

    public static async Task InviaEsitoAsync(Stream flusso, Esito esito, CancellationToken ct = default)
    {
        byte[] pacchetto = new byte[4 + LunghezzaEsito];
        Span<byte> s = pacchetto;
        BinaryPrimitives.WriteInt32BigEndian(s, LunghezzaEsito);
        s[4] = TipoEsito;
        s[5] = (byte)esito.Valore;
        await flusso.WriteAsync(pacchetto, ct);
    }

    private static void ControllaLunghezza(byte[] dati, int attesa, string cosa)
    {
        if (dati.Length != attesa)
            throw new ArgumentException($"{cosa}: {dati.Length} byte invece di {attesa}.");
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
    /// Legge il prossimo messaggio conosciuto (tipi da 1 a 9). Restituisce null se l'altro ha
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
                case TipoAperturaCorsia:
                    return LeggiAperturaCorsia(messaggio);
                case TipoPresentazione:
                    return LeggiPresentazione(messaggio);
                case TipoSfida:
                    return LeggiSfida(messaggio);
                case TipoProva:
                    return LeggiProva(messaggio);
                case TipoEsito:
                    return LeggiEsito(messaggio);
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
        var rotazione = (RotazioneVideo)((messaggio[1] & FlagRotazione) >> SpostamentoRotazione);
        long istante = BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(2));
        var dati = new ReadOnlyMemory<byte>(messaggio, IntestazioneFrame, messaggio.Length - IntestazioneFrame);
        return new FrameVideo(keyframe, istante, dati, rotazione);
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

    private static AperturaCorsia LeggiAperturaCorsia(byte[] messaggio)
    {
        if (messaggio.Length != LunghezzaAperturaCorsia)
            throw new InvalidDataException($"Apertura corsia di {messaggio.Length} byte invece di {LunghezzaAperturaCorsia}.");
        return new AperturaCorsia((RuoloCorsia)messaggio[1], BinaryPrimitives.ReadInt64BigEndian(messaggio.AsSpan(2)));
    }

    private static Presentazione LeggiPresentazione(byte[] messaggio)
    {
        if (messaggio.Length < IntestazionePresentazione || messaggio.Length > IntestazionePresentazione + LunghezzaMassimaNome)
            throw new InvalidDataException($"Presentazione di {messaggio.Length} byte.");
        byte[] chiave = messaggio[1..(1 + Abbinamento.LunghezzaChiave)];
        byte[] impegno = messaggio[(1 + Abbinamento.LunghezzaChiave)..IntestazionePresentazione];
        // UTF-8 non valido diventa "�": il nome serve solo da mostrare.
        string nome = System.Text.Encoding.UTF8.GetString(messaggio, IntestazionePresentazione, messaggio.Length - IntestazionePresentazione);
        return new Presentazione(chiave, impegno, nome);
    }

    private static Sfida LeggiSfida(byte[] messaggio)
    {
        if (messaggio.Length != LunghezzaSfida)
            throw new InvalidDataException($"Sfida di {messaggio.Length} byte invece di {LunghezzaSfida}.");
        return new Sfida(messaggio[1..]);
    }

    private static Prova LeggiProva(byte[] messaggio)
    {
        if (messaggio.Length != LunghezzaProva)
            throw new InvalidDataException($"Prova di {messaggio.Length} byte invece di {LunghezzaProva}.");
        return new Prova(messaggio[1..(1 + Abbinamento.LunghezzaNumero)], messaggio[(1 + Abbinamento.LunghezzaNumero)..]);
    }

    private static Esito LeggiEsito(byte[] messaggio)
    {
        if (messaggio.Length != LunghezzaEsito)
            throw new InvalidDataException($"Esito di {messaggio.Length} byte invece di {LunghezzaEsito}.");
        if (!Enum.IsDefined((ValoreEsito)messaggio[1]))
            throw new InvalidDataException($"Esito sconosciuto: {messaggio[1]}.");
        return new Esito((ValoreEsito)messaggio[1]);
    }

    /// <summary>L'istante attuale nel formato usato da <see cref="FrameVideo.IstanteCatturaMicrosecondi"/>.</summary>
    public static long AdessoMicrosecondi() =>
        (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;
}
