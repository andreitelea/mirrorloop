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

    [Fact]
    public void CodaPienaButtaTuttoEAspettaIlKeyframe()
    {
        var coda = new CodaFrame(capacita: 3);
        coda.Aggiungi(Key(1));
        coda.Aggiungi(Diff(2));
        coda.Aggiungi(Diff(3));
        // La coda è piena: questo frame svuota la coda e, non essendo un keyframe, viene buttato anche lui.
        coda.Aggiungi(Diff(4));
        coda.Aggiungi(Diff(5));
        coda.Aggiungi(Key(6));

        Assert.Equal([6], Svuota(coda));
        Assert.Equal(5, coda.Scartati);
    }

    [Fact]
    public void CodaPienaConKeyframeInArrivoRiparteDaLui()
    {
        var coda = new CodaFrame(capacita: 2);
        coda.Aggiungi(Key(1));
        coda.Aggiungi(Diff(2));
        coda.Aggiungi(Key(3));

        Assert.Equal([3], Svuota(coda));
        Assert.Equal(2, coda.Scartati);
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
