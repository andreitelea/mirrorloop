namespace MirrorLoop.Ricevitore.Test;

public class RiservaAudioTest
{
    // Numeri piccoli per leggere i test a mente: 1000 byte al secondo, blocchi da 4 byte,
    // obiettivo 40 ms = 40 byte, massimo 100 ms = 100 byte.
    private static RiservaAudio Crea() =>
        new(1000, 4, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(100));

    private static byte[] Byte(int quanti, byte valore) => Enumerable.Repeat(valore, quanti).ToArray();

    private static byte[] Leggi(RiservaAudio riserva, int quanti)
    {
        var uscita = new byte[quanti];
        Assert.Equal(quanti, riserva.Leggi(uscita));
        return uscita;
    }

    [Fact]
    public void FinoAllObiettivoSuonaSilenzio()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(36, 7));

        Assert.All(Leggi(riserva, 8), b => Assert.Equal(0, b));
        Assert.Equal(36, riserva.Riserva.TotalMilliseconds, 3);
    }

    [Fact]
    public void RaggiuntoLObiettivoSuonaICampioniInOrdine()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(20, 1));
        riserva.Aggiungi(Byte(20, 2));

        Assert.Equal([.. Byte(20, 1), .. Byte(4, 2)], Leggi(riserva, 24));
        Assert.Equal(Byte(16, 2), Leggi(riserva, 16));
        Assert.Equal(0, riserva.Buchi);
    }

    [Fact]
    public void QuandoFinisconoICampioniCompletaConSilenzioEContaUnBuco()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(40, 5));

        Assert.Equal([.. Byte(40, 5), .. Byte(8, 0)], Leggi(riserva, 48));
        Assert.Equal(1, riserva.Buchi);
    }

    [Fact]
    public void DopoUnBucoAspettaDiNuovoLObiettivo()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(40, 5));
        Leggi(riserva, 48);

        riserva.Aggiungi(Byte(20, 6));
        Assert.All(Leggi(riserva, 8), b => Assert.Equal(0, b));

        riserva.Aggiungi(Byte(20, 6));
        Assert.Equal(Byte(8, 6), Leggi(riserva, 8));
    }

    [Fact]
    public void OltreIlMassimoTornaAllObiettivoTenendoICampioniPiuRecenti()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(60, 1));
        riserva.Aggiungi(Byte(48, 2));

        // 108 byte superano il massimo (100): restano i 40 più recenti.
        Assert.Equal(40, riserva.Riserva.TotalMilliseconds, 3);
        Assert.Equal(1, riserva.Tagli);
        Assert.Equal(Byte(40, 2), Leggi(riserva, 40));
    }

    [Fact]
    public void FunzionaAncheQuandoLAnelloRicominciaDaCapo()
    {
        // L'anello contiene 2 secondi (2000 byte): dopo molti giri i dati devono restare corretti.
        var riserva = Crea();
        for (int giro = 0; giro < 200; giro++)
        {
            byte valore = (byte)(giro % 250 + 1);
            riserva.Aggiungi(Byte(48, valore));
            byte[] letto = Leggi(riserva, 48);
            if (giro > 0)
                Assert.Contains(letto, b => b != 0);
        }
        Assert.Equal(0, riserva.Tagli);
    }

    [Fact]
    public void SvuotaRicominciaDalRiempimento()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(80, 3));
        Leggi(riserva, 8);

        riserva.Svuota();
        riserva.Aggiungi(Byte(20, 4));

        Assert.Equal(TimeSpan.FromMilliseconds(20), riserva.Riserva);
        Assert.All(Leggi(riserva, 8), b => Assert.Equal(0, b));
    }
}
