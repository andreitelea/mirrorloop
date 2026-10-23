namespace MirrorLoop.Ricevitore.Test;

public class CodaImmaginiTest
{
    private readonly List<ImmaginePronta> _restituite = [];

    private CodaImmagini Crea() => new(_restituite.Add);

    private static ImmaginePronta Immagine(long istante) => new(new byte[6], 2, 2, 2, istante, 0);

    [Fact]
    public void SenzaAudioDaSubitoLaPiuRecente()
    {
        var coda = Crea();
        coda.Aggiungi(Immagine(100));
        coda.Aggiungi(Immagine(200));
        coda.Aggiungi(Immagine(300));

        Assert.Equal(300, coda.Prendi(null)!.IstanteCatturaMicrosecondi);
        Assert.Equal(2, coda.NonMostrate);
        Assert.Equal(2, _restituite.Count);
        Assert.Null(coda.Prendi(null));
    }

    [Fact]
    public void ConLAudioDaLaPiuRecenteGiaRaggiunta()
    {
        var coda = Crea();
        coda.Aggiungi(Immagine(100_000));
        coda.Aggiungi(Immagine(133_000));
        coda.Aggiungi(Immagine(166_000));

        // L'audio è a 150 000 µs: l'immagine da 166 000 deve ancora aspettare.
        Assert.Equal(133_000, coda.Prendi(150_000)!.IstanteCatturaMicrosecondi);
        Assert.Equal(1, coda.Conteggio);
        Assert.Equal(1, coda.NonMostrate);
    }

    [Fact]
    public void SeLAudioNonHaAncoraRaggiuntoNessunaImmagineSiAspetta()
    {
        var coda = Crea();
        coda.Aggiungi(Immagine(200_000));

        Assert.Null(coda.Prendi(150_000));
        Assert.Equal(1, coda.Conteggio);

        Assert.Equal(200_000, coda.Prendi(210_000)!.IstanteCatturaMicrosecondi);
    }

    [Fact]
    public void SeLAudioEIndietroDiOltreUnSecondoSiIgnora()
    {
        // Audio fermo o appena ripartito: meglio mostrare il video subito che congelarlo.
        var coda = Crea();
        coda.Aggiungi(Immagine(5_000_000));
        coda.Aggiungi(Immagine(5_033_000));

        Assert.Equal(5_033_000, coda.Prendi(3_000_000)!.IstanteCatturaMicrosecondi);
    }

    [Fact]
    public void OltreIlMassimoSiButtanoLePiuVecchie()
    {
        var coda = Crea();
        for (int i = 0; i < CodaImmagini.Massimo + 5; i++)
            coda.Aggiungi(Immagine(i));

        Assert.Equal(CodaImmagini.Massimo, coda.Conteggio);
        Assert.Equal(5, coda.NonMostrate);
        Assert.Equal([0, 1, 2, 3, 4], _restituite.Select(i => i.IstanteCatturaMicrosecondi));
    }
}
