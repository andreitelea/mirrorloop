using System.Buffers.Binary;

namespace MirrorLoop.Ricevitore.Test;

public class RiservaAudioTest
{
    // Numeri piccoli per leggere i test a mente: 1000 byte al secondo, stereo (blocchi da 4 byte),
    // obiettivo 40 ms = 40 byte, tolleranza 20 ms, massimo 100 ms = 100 byte.
    private static RiservaAudio Crea() =>
        new(1000, 2, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

    // Valori reali: 48 kHz stereo a 16 bit, come dall'iPhone.
    private const int ByteAlSecondoReali = 48_000 * 4;

    private static RiservaAudio CreaReale() =>
        new(ByteAlSecondoReali, 2, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(400));

    private static byte[] Millisecondi(int ms) => Byte(ByteAlSecondoReali / 1000 * ms, 9);

    /// <summary>Campioni stereo con valori crescenti (0, 1, 2…), uguali sui due canali.</summary>
    private static byte[] Rampa(int istanti)
    {
        var dati = new byte[istanti * 4];
        for (int i = 0; i < istanti; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(dati.AsSpan(i * 4), (short)i);
            BinaryPrimitives.WriteInt16LittleEndian(dati.AsSpan(i * 4 + 2), (short)i);
        }
        return dati;
    }

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
    public void AVelocitaNormaleICampioniEsconoIdentici()
    {
        var riserva = CreaReale();
        byte[] rampa = Rampa(2880); // 60 ms: esattamente l'obiettivo, quindi velocità normale
        riserva.Aggiungi(rampa);

        Assert.Equal(rampa[..400], Leggi(riserva, 400));
        Assert.Equal(1, riserva.Velocita);
    }

    [Fact]
    public void ConLaRiservaTroppoPienaSuonaPiuVeloceERecupera()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(150));

        Leggi(riserva, ByteAlSecondoReali / 1000 * 50);

        // 50 ms suonati all'1,005× consumano 50,25 ms: ne restano 99,75 invece di 100.
        Assert.Equal(1 + RiservaAudio.VariazioneVelocita, riserva.Velocita);
        Assert.InRange(riserva.Riserva.TotalMilliseconds, 99.70, 99.80);
        Assert.Equal(0, riserva.Tagli);
    }

    [Fact]
    public void ConLaRiservaQuasiVuotaSuonaPiuLenta()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(60));
        Leggi(riserva, ByteAlSecondoReali / 1000 * 25);   // la riserva scende a 35 ms

        Leggi(riserva, ByteAlSecondoReali / 1000 * 20);

        // 20 ms suonati allo 0,995× consumano 19,9 ms.
        Assert.Equal(1 - RiservaAudio.VariazioneVelocita, riserva.Velocita);
        Assert.InRange(riserva.Riserva.TotalMilliseconds, 15.05, 15.15);
        Assert.Equal(0, riserva.Buchi);
    }

    [Fact]
    public void RitornataAllObiettivoTornaAVelocitaNormale()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(100));
        Leggi(riserva, 4);
        Assert.True(riserva.Velocita > 1);

        // Con nuovi pacchetti da 20 ms e letture da 20 ms la riserva scende piano verso 60 ms.
        for (int i = 0; i < 20_000 && riserva.Velocita > 1; i++)
        {
            riserva.Aggiungi(Millisecondi(20));
            Leggi(riserva, ByteAlSecondoReali / 1000 * 20);
        }

        // La velocità torna normale quando, appena arrivato un pacchetto, la riserva è sull'obiettivo:
        // dopo l'ultima lettura da 20 ms quindi ne restano circa 60 − 20 = 40.
        Assert.Equal(1, riserva.Velocita);
        Assert.InRange(riserva.Riserva.TotalMilliseconds, 39.5, 40.5);
    }

    [Fact]
    public void LInterpolazioneDaValoriIntermedi()
    {
        // Con la velocità aumentata le uscite cadono tra un campione e l'altro della rampa:
        // devono restare comprese tra i valori vicini e sempre crescenti.
        var riserva = CreaReale();
        riserva.Aggiungi(Rampa(7_200)); // 150 ms
        byte[] uscita = Leggi(riserva, 4000);

        short precedente = short.MinValue;
        for (int i = 0; i < uscita.Length; i += 4)
        {
            short sinistro = BinaryPrimitives.ReadInt16LittleEndian(uscita.AsSpan(i));
            short destro = BinaryPrimitives.ReadInt16LittleEndian(uscita.AsSpan(i + 2));
            Assert.Equal(sinistro, destro);
            Assert.True(sinistro >= precedente);
            precedente = sinistro;
        }
        // 1000 istanti all'1,005×: l'ultimo valore è circa 999 × 1,005 ≈ 1004.
        Assert.InRange(precedente, 1003, 1005);
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
