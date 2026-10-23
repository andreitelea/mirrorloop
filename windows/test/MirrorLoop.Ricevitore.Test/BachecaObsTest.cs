using System.Diagnostics;

namespace MirrorLoop.Ricevitore.Test;

public class BachecaObsTest
{
    // Un nome diverso per ogni test: girano in parallelo e non devono vedere la bacheca degli altri.
    private static string NomeUnico() => @"Local\MirrorLoopOBS.Test." + Guid.NewGuid().ToString("N");

    /// <summary>NV12 con passo più largo della larghezza, riempito con un motivo riconoscibile.</summary>
    private static byte[] Nv12(int larghezza, int altezza, int passo, byte seme)
    {
        var dati = new byte[passo * altezza * 3 / 2];
        for (int i = 0; i < dati.Length; i++)
            dati[i] = (byte)(i * 7 + seme);
        return dati;
    }

    /// <summary>Lo stesso NV12 con le righe compatte, come deve uscire dalla bacheca.</summary>
    private static byte[] Compatto(byte[] nv12, int larghezza, int altezza, int passo)
    {
        int righe = altezza * 3 / 2;
        var compatto = new byte[larghezza * righe];
        for (int riga = 0; riga < righe; riga++)
            nv12.AsSpan(riga * passo, larghezza).CopyTo(compatto.AsSpan(riga * larghezza));
        return compatto;
    }

    [Fact]
    public void LIntestazioneDescriveLaBacheca()
    {
        string nome = NomeUnico();
        ulong prima = BachecaObs.OraNs();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);

        Assert.Equal("MLOB", lettore.Firma);
        Assert.Equal(1u, lettore.Versione);
        Assert.Equal(25_694_208ul, lettore.Dimensione);
        Assert.Equal(3u, lettore.NumeroCassetti);
        Assert.Equal(8_388_608u, lettore.DimensioneCassetto);
        Assert.Equal(128u, lettore.NumeroBlocchi);
        Assert.Equal(4096u, lettore.DimensioneBlocco);
        Assert.Equal((uint)Environment.ProcessId, lettore.Processo);
        Assert.Equal(1ul, lettore.Generazione);
        Assert.Equal(0ul, lettore.ImmaginiScritte);
        Assert.Equal(0ul, lettore.BlocchiScritti);
        Assert.InRange(lettore.Battito, prima, BachecaObs.OraNs());
        Assert.Null(lettore.UltimaImmagine());
    }

    [Fact]
    public void LOrologioEQuelloDiObs()
    {
        // os_gettime_ns di libobs: QueryPerformanceCounter × 10⁹ / frequenza (lo stesso contatore di Stopwatch).
        long contatore = Stopwatch.GetTimestamp();
        double atteso = contatore * 1e9 / Stopwatch.Frequency;
        Assert.Equal(atteso, BachecaObs.DaContatore(contatore), 1e9 / Stopwatch.Frequency + 1);

        ulong ora = BachecaObs.OraNs();
        Assert.InRange(ora, BachecaObs.DaContatore(contatore), BachecaObs.DaContatore(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void UnImmagineSiRileggeUgualeConLeRigheCompatte()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);
        byte[] nv12 = Nv12(1088, 2352, 1152, 3);

        bacheca.ScriviImmagine(nv12, 1088, 2352, 1152, 123_456_789);

        var immagine = lettore.UltimaImmagine();
        Assert.NotNull(immagine);
        Assert.Equal(1ul, immagine.Numero);
        Assert.Equal(123_456_789ul, immagine.Istante);
        Assert.Equal(1ul, immagine.Generazione);
        Assert.Equal((1088, 2352, 1088, 1088), (immagine.Larghezza, immagine.Altezza, immagine.PassoY, immagine.PassoUv));
        Assert.Equal(BachecaObs.FormatoNv12, immagine.Formato);
        Assert.Equal(BachecaObs.ColoreBt709Limitato, immagine.Colore);
        Assert.Equal(Compatto(nv12, 1088, 2352, 1152), immagine.Dati);
    }

    [Fact]
    public void SiLeggeSempreLUltimaImmagineAncheDopoIlGiroDeiCassetti()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);

        for (byte i = 1; i <= 7; i++)
            bacheca.ScriviImmagine(Nv12(16, 8, 16, i), 16, 8, 16, i * 1000ul);

        var immagine = lettore.UltimaImmagine()!;
        Assert.Equal(7ul, immagine.Numero);
        Assert.Equal(7000ul, immagine.Istante);
        Assert.Equal(Nv12(16, 8, 16, 7), immagine.Dati);
    }

    [Fact]
    public void UnaCopiaMistaVieneScartata()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);
        bacheca.ScriviImmagine(Nv12(16, 8, 16, 1), 16, 8, 16, 1000);

        // A metà della copia il ricevitore fa il giro dei 3 cassetti e riscrive proprio quello in lettura.
        lettore.DuranteLaCopia = () =>
        {
            for (byte i = 2; i <= 4; i++)
                bacheca.ScriviImmagine(Nv12(16, 8, 16, i), 16, 8, 16, i * 1000ul);
        };
        Assert.Null(lettore.UltimaImmagine());

        lettore.DuranteLaCopia = null;
        Assert.Equal(4ul, lettore.UltimaImmagine()!.Numero);
    }

    [Fact]
    public void LAudioSiRileggeInOrdineEIBlocchiLunghiSiDividono()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);

        // 20 ms di stereo a 48 kHz (3840 byte) stanno in un blocco; 50 ms (9600 byte) no.
        byte[] corto = Enumerable.Range(0, 3840).Select(i => (byte)i).ToArray();
        byte[] lungo = Enumerable.Range(0, 9600).Select(i => (byte)(i * 3)).ToArray();
        bacheca.ScriviAudio(corto, 2, 48_000, 1_000_000_000);
        bacheca.ScriviAudio(lungo, 2, 48_000, 1_020_000_000);

        var blocchi = lettore.NuoviBlocchi();
        Assert.Equal(4, blocchi.Count); // 1 + 3 (1008 + 1008 + 384 campioni)
        Assert.Equal([0ul, 1, 2, 3], blocchi.Select(b => b.Numero));
        Assert.Equal(corto, blocchi[0].Dati);
        Assert.Equal(lungo, blocchi.Skip(1).SelectMany(b => b.Dati));
        Assert.Equal([960, 1008, 1008, 384], blocchi.Select(b => b.Campioni));
        Assert.Equal([1_000_000_000ul, 1_020_000_000, 1_041_000_000, 1_062_000_000], blocchi.Select(b => b.Istante));
        Assert.All(blocchi, b => Assert.Equal((2, 48_000, BachecaObs.FormatoPcm16), (b.Canali, b.Frequenza, b.Formato)));

        Assert.Empty(lettore.NuoviBlocchi());
        Assert.Equal(0ul, lettore.BlocchiPersi);
    }

    [Fact]
    public void ChiResteIndietroSulNastroPerdeSoloIBlocchiSovrascritti()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);

        for (int i = 0; i < 130; i++)
            bacheca.ScriviAudio(new byte[] { (byte)i, 0, (byte)i, 0 }, 2, 48_000, (ulong)i);

        var blocchi = lettore.NuoviBlocchi();
        Assert.Equal(2ul, lettore.BlocchiPersi);
        Assert.Equal(128, blocchi.Count);
        Assert.Equal(2ul, blocchi[0].Numero);
        Assert.Equal(129ul, blocchi[^1].Numero);
        Assert.Equal((byte)129, blocchi[^1].Dati[0]);
    }

    [Fact]
    public void LaGenerazioneCambiaEFiniscePuntoPerPunto()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);

        bacheca.NuovaGenerazione();
        bacheca.ScriviImmagine(Nv12(16, 8, 16, 1), 16, 8, 16, 1);
        bacheca.ScriviAudio(new byte[4], 2, 48_000, 1);

        Assert.Equal(2ul, lettore.Generazione);
        Assert.Equal(2ul, lettore.UltimaImmagine()!.Generazione);
        Assert.Equal(2ul, lettore.NuoviBlocchi().Single().Generazione);
    }

    [Fact]
    public void OgniScritturaSuonaIlCampanello()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);

        Assert.False(lettore.Nuovo.WaitOne(0));
        bacheca.ScriviImmagine(Nv12(16, 8, 16, 1), 16, 8, 16, 1);
        Assert.True(lettore.Nuovo.WaitOne(0));
        bacheca.ScriviAudio(new byte[4], 2, 48_000, 1);
        Assert.True(lettore.Nuovo.WaitOne(0));
    }

    [Fact]
    public void IlBattitoAvanza()
    {
        string nome = NomeUnico();
        using var bacheca = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome);

        ulong primo = lettore.Battito;
        Thread.Sleep(5);
        bacheca.Battito();
        Assert.True(lettore.Battito > primo);
    }

    [Fact]
    public void UnRicevitoreRiapertoContinuaIContatori()
    {
        string nome = NomeUnico();
        var prima = new BachecaObs(nome);
        using var lettore = new LettoreBacheca(nome); // il plugin tiene viva la memoria
        prima.ScriviImmagine(Nv12(16, 8, 16, 1), 16, 8, 16, 1);
        prima.ScriviAudio(new byte[8], 2, 48_000, 1);
        lettore.NuoviBlocchi();
        prima.Dispose();

        using var dopo = new BachecaObs(nome);
        Assert.Equal(1ul, lettore.ImmaginiScritte); // non tornano a zero
        Assert.Equal(1ul, lettore.BlocchiScritti);
        Assert.Equal(2ul, lettore.Generazione);

        dopo.ScriviImmagine(Nv12(16, 8, 16, 9), 16, 8, 16, 9);
        dopo.ScriviAudio(new byte[8], 2, 48_000, 9);
        Assert.Equal((2ul, 2ul), (lettore.UltimaImmagine()!.Numero, lettore.UltimaImmagine()!.Generazione));
        Assert.Equal(1ul, lettore.NuoviBlocchi().Single().Numero);
        Assert.Equal(0ul, lettore.BlocchiPersi);
    }

    [Fact]
    public void DopoLaChiusuraBattitoEGenerazioneNonFannoDanni()
    {
        var bacheca = new BachecaObs(NomeUnico());
        bacheca.Dispose();

        bacheca.Battito();
        bacheca.NuovaGenerazione();
        Assert.Throws<ObjectDisposedException>(() => bacheca.ScriviAudio(new byte[4], 2, 48_000, 1));
    }

    [Theory]
    [InlineData(15, 8, 16)]   // larghezza dispari
    [InlineData(16, 7, 16)]   // altezza dispari
    [InlineData(16, 8, 8)]    // passo minore della larghezza
    [InlineData(4096, 2048, 4096)] // oltre gli 8 MB di un cassetto
    public void LeImmaginiNonValideSonoRifiutate(int larghezza, int altezza, int passo)
    {
        using var bacheca = new BachecaObs(NomeUnico());
        var dati = new byte[Math.Max(passo, 1) * Math.Max(altezza, 1) * 2];
        Assert.Throws<ArgumentException>(() => bacheca.ScriviImmagine(dati, larghezza, altezza, passo, 1));
    }

    [Fact]
    public void LAudioConCampioniSpezzatiERifiutato()
    {
        using var bacheca = new BachecaObs(NomeUnico());
        Assert.Throws<ArgumentException>(() => bacheca.ScriviAudio(new byte[6], 2, 48_000, 1));
    }
}
