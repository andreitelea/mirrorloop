using System.Buffers.Binary;

namespace MirrorLoop.Protocollo.Test;

public class ProtocolloTest
{
    private static FrameVideo CreaFrame(bool keyframe, long istante, params byte[] dati) =>
        new(keyframe, istante, dati);

    private static async Task<MemoryStream> ScriviAsync(params FrameVideo[] frame)
    {
        var flusso = new MemoryStream();
        foreach (var f in frame)
            await Protocollo.InviaFrameAsync(flusso, f);
        flusso.Position = 0;
        return flusso;
    }

    [Fact]
    public async Task UnFrameAndataERitorno()
    {
        var originale = CreaFrame(true, 1_729_000_000_123_456, 0, 0, 0, 1, 0x67, 0x42);
        using var flusso = await ScriviAsync(originale);

        var letto = await Protocollo.RiceviFrameAsync(flusso);

        Assert.NotNull(letto);
        Assert.True(letto.Keyframe);
        Assert.Equal(originale.IstanteCatturaMicrosecondi, letto.IstanteCatturaMicrosecondi);
        Assert.Equal(originale.DatiH264.ToArray(), letto.DatiH264.ToArray());
    }

    [Fact]
    public async Task PiuFrameDiFilaRestanoSeparati()
    {
        using var flusso = await ScriviAsync(
            CreaFrame(true, 1, 1, 2, 3),
            CreaFrame(false, 2, 4),
            CreaFrame(false, 3, 5, 6));

        var a = await Protocollo.RiceviFrameAsync(flusso);
        var b = await Protocollo.RiceviFrameAsync(flusso);
        var c = await Protocollo.RiceviFrameAsync(flusso);

        Assert.Equal([1, 2, 3], a!.DatiH264.ToArray());
        Assert.Equal([4], b!.DatiH264.ToArray());
        Assert.Equal([5, 6], c!.DatiH264.ToArray());
        Assert.True(a.Keyframe);
        Assert.False(b.Keyframe);
        Assert.Equal(3, c.IstanteCatturaMicrosecondi);
    }

    [Fact]
    public async Task ChiusuraPulitaRestituisceNull()
    {
        using var flusso = await ScriviAsync(CreaFrame(false, 0, 9));

        await Protocollo.RiceviFrameAsync(flusso);
        var dopo = await Protocollo.RiceviFrameAsync(flusso);

        Assert.Null(dopo);
    }

    [Fact]
    public async Task FunzionaAncheSeIByteArrivanoUnoAllaVolta()
    {
        // Simula il caso peggiore di TCP: ogni lettura restituisce un solo byte.
        var dati = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        using var completo = await ScriviAsync(CreaFrame(true, 42, dati), CreaFrame(false, 43, 7));
        using var aGoccia = new FlussoAGoccia(completo.ToArray());

        var a = await Protocollo.RiceviFrameAsync(aGoccia);
        var b = await Protocollo.RiceviFrameAsync(aGoccia);

        Assert.Equal(dati, a!.DatiH264.ToArray());
        Assert.Equal(43, b!.IstanteCatturaMicrosecondi);
    }

    [Fact]
    public async Task InterruzioneAMetaMessaggioDaErrore()
    {
        using var completo = await ScriviAsync(CreaFrame(true, 1, 1, 2, 3, 4, 5));
        byte[] troncato = completo.ToArray()[..^2];
        using var flusso = new MemoryStream(troncato);

        await Assert.ThrowsAsync<EndOfStreamException>(() => Protocollo.RiceviFrameAsync(flusso));
    }

    [Fact]
    public async Task InterruzioneAMetaPrefissoDaErrore()
    {
        using var flusso = new MemoryStream([0, 0]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => Protocollo.RiceviFrameAsync(flusso));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Protocollo.LunghezzaMassima + 1)]
    public async Task LunghezzaNonValidaDaErrore(int lunghezza)
    {
        byte[] prefisso = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefisso, lunghezza);
        using var flusso = new MemoryStream(prefisso);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviFrameAsync(flusso));
    }

    [Fact]
    public async Task TipoSconosciutoVieneSaltato()
    {
        // Un messaggio di tipo 99 (inventato) seguito da un frame normale.
        var flusso = new MemoryStream();
        flusso.Write([0, 0, 0, 3, 99, 0xAA, 0xBB]);
        await Protocollo.InviaFrameAsync(flusso, CreaFrame(false, 7, 1));
        flusso.Position = 0;

        var letto = await Protocollo.RiceviFrameAsync(flusso);

        Assert.Equal(7, letto!.IstanteCatturaMicrosecondi);
    }

    [Fact]
    public async Task FrameVideoSenzaDatiInArrivoDaErrore()
    {
        // Lunghezza 10 = solo intestazione (tipo + flag + istante), nessun dato H.264.
        byte[] messaggio = [0, 0, 0, 10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        using var flusso = new MemoryStream(messaggio);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviFrameAsync(flusso));
    }

    [Fact]
    public async Task NonSiPuoInviareUnFrameVuoto()
    {
        using var flusso = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Protocollo.InviaFrameAsync(flusso, CreaFrame(true, 0)));
    }

    [Fact]
    public async Task IlFormatoSulFiloEQuelloDocumentato()
    {
        // Protegge il formato da modifiche involontarie: l'app iPhone dovrà produrre esattamente questi byte.
        using var flusso = await ScriviAsync(CreaFrame(true, 0x0102030405060708, 0xAB, 0xCD));

        byte[] atteso =
        [
            0, 0, 0, 12,                                // lunghezza: tipo + flag + istante + 2 byte di dati
            1,                                          // tipo: frame video
            1,                                          // flag: keyframe
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // istante di cattura
            0xAB, 0xCD,                                 // dati H.264
        ];
        Assert.Equal(atteso, flusso.ToArray());
    }

    private static PacchettoAudio CreaAudio(long istante, params byte[] dati) =>
        new(CodificaAudio.Pcm16, 2, 48_000, istante, dati);

    [Fact]
    public async Task UnPacchettoAudioAndataERitorno()
    {
        var originale = CreaAudio(1_729_000_000_654_321, 1, 2, 3, 4, 5, 6, 7, 8);
        var flusso = new MemoryStream();
        await Protocollo.InviaAudioAsync(flusso, originale);
        flusso.Position = 0;

        var letto = await Protocollo.RiceviMessaggioAsync(flusso);

        var audio = Assert.IsType<PacchettoAudio>(letto);
        Assert.Equal(CodificaAudio.Pcm16, audio.Codifica);
        Assert.Equal(2, audio.Canali);
        Assert.Equal(48_000, audio.Frequenza);
        Assert.Equal(originale.IstanteCatturaMicrosecondi, audio.IstanteCatturaMicrosecondi);
        Assert.Equal(originale.Dati.ToArray(), audio.Dati.ToArray());
    }

    [Fact]
    public async Task VideoEAudioMescolatiArrivanoInOrdine()
    {
        var flusso = new MemoryStream();
        await Protocollo.InviaFrameAsync(flusso, CreaFrame(true, 1, 9));
        await Protocollo.InviaAudioAsync(flusso, CreaAudio(2, 0, 0, 0, 0));
        await Protocollo.InviaFrameAsync(flusso, CreaFrame(false, 3, 8));
        flusso.Position = 0;

        Assert.IsType<FrameVideo>(await Protocollo.RiceviMessaggioAsync(flusso));
        Assert.IsType<PacchettoAudio>(await Protocollo.RiceviMessaggioAsync(flusso));
        var ultimo = Assert.IsType<FrameVideo>(await Protocollo.RiceviMessaggioAsync(flusso));
        Assert.Equal(3, ultimo.IstanteCatturaMicrosecondi);
        Assert.Null(await Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task RiceviFrameSaltaLAudio()
    {
        // Un ricevitore che conosce solo il video (come quello della Fase 3) continua a funzionare.
        var flusso = new MemoryStream();
        await Protocollo.InviaAudioAsync(flusso, CreaAudio(1, 0, 0, 0, 0));
        await Protocollo.InviaFrameAsync(flusso, CreaFrame(false, 5, 1));
        flusso.Position = 0;

        var letto = await Protocollo.RiceviFrameAsync(flusso);

        Assert.Equal(5, letto!.IstanteCatturaMicrosecondi);
    }

    [Fact]
    public async Task IlFormatoAudioSulFiloEQuelloDocumentato()
    {
        // L'app iPhone deve produrre esattamente questi byte (vedi docs/protocollo.md).
        var flusso = new MemoryStream();
        await Protocollo.InviaAudioAsync(flusso, CreaAudio(0x0102030405060708, 0x10, 0x20, 0x30, 0x40));

        byte[] atteso =
        [
            0, 0, 0, 19,                                // lunghezza: 15 di intestazione + 4 byte di campioni
            2,                                          // tipo: audio
            1,                                          // codifica: PCM 16 bit
            2,                                          // canali: stereo
            0x00, 0x00, 0xBB, 0x80,                     // frequenza: 48 000
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // istante di cattura
            0x10, 0x20, 0x30, 0x40,                     // un campione sinistro e uno destro
        ];
        Assert.Equal(atteso, flusso.ToArray());
    }

    [Fact]
    public async Task AudioPcmConCampioniSpezzatiDaErrore()
    {
        // Stereo a 16 bit = 4 byte per istante: 3 byte di dati non sono validi.
        byte[] messaggio = [0, 0, 0, 18, 2, 1, 2, 0, 0, 0xBB, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3];
        using var flusso = new MemoryStream(messaggio);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task AudioSenzaCanaliDaErrore()
    {
        byte[] messaggio = [0, 0, 0, 17, 2, 1, 0, 0, 0, 0xBB, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2];
        using var flusso = new MemoryStream(messaggio);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task AudioSenzaCampioniDaErrore()
    {
        byte[] messaggio = [0, 0, 0, 15, 2, 1, 2, 0, 0, 0xBB, 0x80, 0, 0, 0, 0, 0, 0, 0, 0];
        using var flusso = new MemoryStream(messaggio);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task CodificaAudioSconosciutaArrivaComunque()
    {
        // Codifica 7 (inventata): il protocollo la consegna, sarà il ricevitore a ignorarla.
        byte[] messaggio = [0, 0, 0, 17, 2, 7, 2, 0, 0, 0xBB, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2];
        using var flusso = new MemoryStream(messaggio);

        var audio = Assert.IsType<PacchettoAudio>(await Protocollo.RiceviMessaggioAsync(flusso));

        Assert.Equal((CodificaAudio)7, audio.Codifica);
        Assert.Equal(TimeSpan.Zero, audio.Durata);
    }

    [Fact]
    public void LaDurataDiUnPacchettoPcmSiCalcolaDaiByte()
    {
        // 960 istanti stereo a 16 bit = 3840 byte = 20 ms a 48 kHz.
        var audio = new PacchettoAudio(CodificaAudio.Pcm16, 2, 48_000, 0, new byte[3840]);

        Assert.Equal(TimeSpan.FromMilliseconds(20), audio.Durata);
    }

    [Fact]
    public async Task PingEPongAndataERitorno()
    {
        var flusso = new MemoryStream();
        await Protocollo.InviaPingAsync(flusso, new Ping(7, 1_000));
        await Protocollo.InviaPongAsync(flusso, new Pong(7, 1_000, 2_000, 2_050));
        flusso.Position = 0;

        Assert.Equal(new Ping(7, 1_000), await Protocollo.RiceviMessaggioAsync(flusso));
        Assert.Equal(new Pong(7, 1_000, 2_000, 2_050), await Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task IlFormatoDiPingEPongSulFiloEQuelloDocumentato()
    {
        // L'app iPhone deve leggere il ping e scrivere il pong esattamente così (docs/protocollo.md).
        var flusso = new MemoryStream();
        await Protocollo.InviaPingAsync(flusso, new Ping(0x0A0B0C0D, 0x0102030405060708));
        await Protocollo.InviaPongAsync(flusso, new Pong(0x0A0B0C0D, 0x0102030405060708, 0x1112131415161718, 0x2122232425262728));

        byte[] atteso =
        [
            0, 0, 0, 13,                                       // ping: lunghezza = tipo + numero + T1
            3,                                                 // tipo: ping
            0x0A, 0x0B, 0x0C, 0x0D,                            // numero
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,    // T1
            0, 0, 0, 29,                                       // pong: lunghezza = tipo + numero + T1 + T2 + T3
            4,                                                 // tipo: pong
            0x0A, 0x0B, 0x0C, 0x0D,                            // numero
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,    // T1 (copiato dal ping)
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,    // T2
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,    // T3
        ];
        Assert.Equal(atteso, flusso.ToArray());
    }

    [Fact]
    public async Task UnPongDiLunghezzaSbagliataDaErrore()
    {
        byte[] messaggio = [0, 0, 0, 5, 4, 0, 0, 0, 1];
        using var flusso = new MemoryStream(messaggio);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task IlFormatoDellAperturaCorsiaSulFiloEQuelloDocumentato()
    {
        // L'app iPhone deve scrivere l'apertura esattamente così (docs/protocollo.md).
        var flusso = new MemoryStream();
        await Protocollo.InviaAperturaCorsiaAsync(flusso, new AperturaCorsia(RuoloCorsia.Video, 0x0102030405060708));

        byte[] atteso =
        [
            0, 0, 0, 10,                                       // lunghezza = tipo + ruolo + numero
            5,                                                 // tipo: apertura corsia
            2,                                                 // ruolo: video
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,    // numero di trasmissione
        ];
        Assert.Equal(atteso, flusso.ToArray());

        flusso.Position = 0;
        Assert.Equal(new AperturaCorsia(RuoloCorsia.Video, 0x0102030405060708), await Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task UnNumeroDiTrasmissioneNegativoArrivaIntatto()
    {
        // L'iPhone sceglie 64 bit a caso: metà dei numeri hanno il bit più alto a 1.
        var flusso = new MemoryStream();
        await Protocollo.InviaAperturaCorsiaAsync(flusso, new AperturaCorsia(RuoloCorsia.Controllo, -2));
        flusso.Position = 0;

        var letta = Assert.IsType<AperturaCorsia>(await Protocollo.RiceviMessaggioAsync(flusso));
        Assert.Equal(-2, letta.NumeroTrasmissione);
        Assert.Equal(RuoloCorsia.Controllo, letta.Ruolo);
    }

    [Fact]
    public async Task UnaAperturaCorsiaVuotaDaErrore()
    {
        // Il formato delle build 0.7.0 e 0.7.1 dell'app, senza ruolo né numero.
        byte[] messaggio = [0, 0, 0, 1, 5];
        using var flusso = new MemoryStream(messaggio);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(flusso));
    }

    /// <summary>Un flusso che restituisce al massimo un byte per lettura.</summary>
    private sealed class FlussoAGoccia(byte[] dati) : MemoryStream(dati)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            base.ReadAsync(buffer, offset, Math.Min(count, 1), ct);
    }
}
