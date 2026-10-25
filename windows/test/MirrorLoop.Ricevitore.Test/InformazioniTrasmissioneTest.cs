namespace MirrorLoop.Ricevitore.Test;

public sealed class InformazioniTrasmissioneTest
{
    [Theory]
    [InlineData(720, 1552, "HD")]
    [InlineData(1088, 2352, "Full HD")]
    [InlineData(1170, 2532, "Massima")]
    [InlineData(1320, 2868, "Massima")]
    [InlineData(2352, 1088, "Full HD")]
    public void LaQualitàSiRiconosceDalLatoCorto(int larghezza, int altezza, string attesa) =>
        Assert.Equal(attesa, InformazioniTrasmissione.Qualita(larghezza, altezza));

    [Fact]
    public void IlRitardoÈInDecimiDiSecondo() =>
        Assert.Equal("circa 0,1 s", InformazioniTrasmissione.Ritardo(112.4));

    [Theory]
    [InlineData(0, "nessuna nell'ultimo minuto")]
    [InlineData(1, "1 nell'ultimo minuto")]
    [InlineData(4, "4 nell'ultimo minuto")]
    public void LeInterruzioniSiContano(long quante, string attesa) =>
        Assert.Equal(attesa, InformazioniTrasmissione.Interruzioni(quante));

    [Fact]
    public void IlContoGuardaSoloLUltimoMinuto()
    {
        var conto = new ContoUltimoMinuto(TimeSpan.FromMinutes(1));

        Assert.Equal(0, conto.Aggiungi(TimeSpan.FromSeconds(0), 3));
        Assert.Equal(2, conto.Aggiungi(TimeSpan.FromSeconds(30), 5));
        // A 90 s il campione di 0 s è uscito dalla finestra: conta solo da 30 s in poi.
        Assert.Equal(1, conto.Aggiungi(TimeSpan.FromSeconds(90), 6));
        Assert.Equal(0, conto.Aggiungi(TimeSpan.FromSeconds(200), 6));
    }

    [Fact]
    public void UnaNuovaConnessioneRipartendoDaZeroNonDàNumeriNegativi()
    {
        var conto = new ContoUltimoMinuto(TimeSpan.FromMinutes(1));
        conto.Aggiungi(TimeSpan.FromSeconds(0), 10);

        Assert.Equal(0, conto.Aggiungi(TimeSpan.FromSeconds(1), 0));
        Assert.Equal(2, conto.Aggiungi(TimeSpan.FromSeconds(2), 2));
    }
}
