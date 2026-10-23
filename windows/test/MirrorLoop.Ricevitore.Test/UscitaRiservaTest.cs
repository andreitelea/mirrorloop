using NAudio.Wave;

namespace MirrorLoop.Ricevitore.Test;

public sealed class UscitaRiservaTest : IDisposable
{
    private readonly string _nome = @"Local\MirrorLoopOBS.Test." + Guid.NewGuid().ToString("N");
    private readonly BachecaObs _bacheca;
    private readonly LettoreBacheca _lettore;

    public UscitaRiservaTest()
    {
        _bacheca = new BachecaObs(_nome);
        _lettore = new LettoreBacheca(_nome);
    }

    public void Dispose()
    {
        _lettore.Dispose();
        _bacheca.Dispose();
    }

    // 48 kHz stereo a 16 bit come dall'iPhone; riserva da 60 ms, senza dissolvenze.
    private static RiservaAudio Riserva() => new(48_000 * 4, 2, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(400), TimeSpan.Zero);

    private static byte[] Audio(int ms)
    {
        var dati = new byte[48 * 4 * ms];
        for (int i = 0; i < dati.Length; i++)
            dati[i] = (byte)(i * 13);
        return dati;
    }

    [Fact]
    public void OBSRiceveEsattamenteQuelloCheVaAllaSchedaAudio()
    {
        var riserva = Riserva();
        riserva.Aggiungi(Audio(100));
        var uscita = new RiproduzioneAudio.UscitaRiserva(riserva, new WaveFormat(48_000, 16, 2), _bacheca, TimeSpan.FromMilliseconds(30));

        // Come WASAPI: letture da 10 ms, in un buffer più grande con uno scostamento.
        var suonati = new List<byte>();
        var buffer = new byte[5000];
        for (int i = 0; i < 5; i++)
        {
            int letti = uscita.Read(buffer, 100, 1920);
            Assert.Equal(1920, letti);
            suonati.AddRange(buffer.AsSpan(100, letti).ToArray());
        }

        var blocchi = _lettore.NuoviBlocchi();
        Assert.Equal(5, blocchi.Count);
        Assert.Equal(suonati, blocchi.SelectMany(b => b.Dati));
        Assert.All(blocchi, b => Assert.Equal((480, 2, 48_000), (b.Campioni, b.Canali, b.Frequenza)));
    }

    [Fact]
    public void LIstanteEQuelloInCuiSiSentira()
    {
        var riserva = Riserva();
        riserva.Aggiungi(Audio(100));
        var uscita = new RiproduzioneAudio.UscitaRiserva(riserva, new WaveFormat(48_000, 16, 2), _bacheca, TimeSpan.FromMilliseconds(30));

        ulong prima = BachecaObs.OraNs();
        uscita.Read(new byte[1920], 0, 1920);
        ulong dopo = BachecaObs.OraNs();

        // Adesso più i 30 ms della latenza d'uscita.
        Assert.InRange(_lettore.NuoviBlocchi().Single().Istante, prima + 30_000_000, dopo + 30_000_000);
    }

    [Fact]
    public void ConLaRiservaVuotaAOBSVaSilenzioComeAlleCuffie()
    {
        // Senza audio in arrivo la scheda audio riceve silenzio: anche OBS, così il suo tempo non si ferma.
        var uscita = new RiproduzioneAudio.UscitaRiserva(Riserva(), new WaveFormat(48_000, 16, 2), _bacheca, TimeSpan.FromMilliseconds(30));

        uscita.Read(new byte[1920], 0, 1920);

        Assert.All(_lettore.NuoviBlocchi().Single().Dati, b => Assert.Equal(0, b));
    }

    [Fact]
    public void SenzaBachecaSuonaLoStesso()
    {
        var riserva = Riserva();
        riserva.Aggiungi(Audio(100));
        var uscita = new RiproduzioneAudio.UscitaRiserva(riserva, new WaveFormat(48_000, 16, 2), null, TimeSpan.FromMilliseconds(30));

        Assert.Equal(1920, uscita.Read(new byte[1920], 0, 1920));
    }

    [Fact]
    public void ConLaBachecaGiaChiusaNonSiFermaLaSchedaAudio()
    {
        var bacheca = new BachecaObs(_nome + ".Chiusa");
        var uscita = new RiproduzioneAudio.UscitaRiserva(Riserva(), new WaveFormat(48_000, 16, 2), bacheca, TimeSpan.FromMilliseconds(30));
        bacheca.Dispose();

        Assert.Equal(1920, uscita.Read(new byte[1920], 0, 1920));
    }
}
