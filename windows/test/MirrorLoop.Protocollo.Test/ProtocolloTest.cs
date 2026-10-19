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
