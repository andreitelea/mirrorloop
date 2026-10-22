using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore.Test;

public class CodaFrameTest
{
    private static FrameVideo Key(long n) => new(true, n, new byte[] { 1 });
    private static FrameVideo Diff(long n) => new(false, n, new byte[] { 2 });

    private static List<long> Svuota(CodaFrame coda)
    {
        var presi = new List<long>();
        while (coda.ProvaPrendi(TimeSpan.Zero, out var f))
            presi.Add(f!.IstanteCatturaMicrosecondi);
        return presi;
    }

    [Fact]
    public void ScartaTuttoFinoAlPrimoKeyframe()
    {
        var coda = new CodaFrame();
        coda.Aggiungi(Diff(1));
        coda.Aggiungi(Diff(2));
        coda.Aggiungi(Key(3));
        coda.Aggiungi(Diff(4));

        Assert.Equal([3, 4], Svuota(coda));
        Assert.Equal(2, coda.Scartati);
    }

    // Istanti in millisecondi, per leggere i test a mente.
    private static FrameVideo KeyMs(long ms) => Key(ms * 1000);
    private static FrameVideo DiffMs(long ms) => Diff(ms * 1000);

    private static List<long> SvuotaMs(CodaFrame coda) => Svuota(coda).Select(us => us / 1000).ToList();

    [Fact]
    public void CodaPienaButtaTuttoEAspettaIlKeyframe()
    {
        var coda = new CodaFrame(TimeSpan.FromMilliseconds(500));
        coda.Aggiungi(KeyMs(0));
        coda.Aggiungi(DiffMs(250));
        coda.Aggiungi(DiffMs(500));
        // Oltre 500 ms dal più vecchio in coda: questo frame svuota la coda e, non essendo un
        // keyframe, viene buttato anche lui.
        coda.Aggiungi(DiffMs(501));
        coda.Aggiungi(DiffMs(520));
        coda.Aggiungi(KeyMs(540));

        Assert.Equal([540], SvuotaMs(coda));
        Assert.Equal(5, coda.Scartati);
    }

    [Fact]
    public void CodaPienaConKeyframeInArrivoRiparteDaLui()
    {
        var coda = new CodaFrame(TimeSpan.FromMilliseconds(500));
        coda.Aggiungi(KeyMs(0));
        coda.Aggiungi(DiffMs(17));
        coda.Aggiungi(KeyMs(600));

        Assert.Equal([600], SvuotaMs(coda));
        Assert.Equal(2, coda.Scartati);
    }

    [Fact]
    public void UnaRafficaA60FpsDopoUnaPausaNonSvuotaLaCoda()
    {
        // Il caso della prova del 22/10/2026: dopo una pausa del Wi-Fi arrivano insieme 9 frame
        // (150 ms di video) mentre il decompressore non ne ha ancora preso nessuno.
        var coda = new CodaFrame();
        coda.Aggiungi(KeyMs(0));
        for (int n = 1; n <= 9; n++)
            coda.Aggiungi(DiffMs(n * 1000 / 60));

        Assert.Equal(10, Svuota(coda).Count);
        Assert.Equal(0, coda.Scartati);
    }

    [Fact]
    public void RicominciaSvuotaEAspettaUnNuovoKeyframe()
    {
        var coda = new CodaFrame();
        coda.Aggiungi(Key(1));
        coda.Aggiungi(Diff(2));

        coda.Ricomincia();
        coda.Aggiungi(Diff(3));
        coda.Aggiungi(Key(4));

        Assert.Equal([4], Svuota(coda));
    }

    [Fact]
    public void ProvaPrendiSuCodaVuotaAspettaEPoiRestituisceFalse()
    {
        var coda = new CodaFrame();
        var orologio = System.Diagnostics.Stopwatch.StartNew();

        bool preso = coda.ProvaPrendi(TimeSpan.FromMilliseconds(100), out _);

        Assert.False(preso);
        Assert.True(orologio.ElapsedMilliseconds >= 90);
    }

    [Fact]
    public async Task ProvaPrendiSiSvegliaQuandoArrivaUnFrame()
    {
        var coda = new CodaFrame();
        var attesa = Task.Run(() => coda.ProvaPrendi(TimeSpan.FromSeconds(5), out var f) ? f : null);

        await Task.Delay(50);
        coda.Aggiungi(Key(7));
        var preso = await attesa.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(7, preso!.IstanteCatturaMicrosecondi);
    }
}
