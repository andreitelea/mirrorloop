using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore.Test;

public sealed class PresentazioneTest : IDisposable
{
    private readonly string _nome = @"Local\MirrorLoopOBS.Test." + Guid.NewGuid().ToString("N");
    private readonly List<ImmaginePronta> _restituite = [];
    private readonly CodaImmagini _coda;
    private readonly AutoResetEvent _nuova = new(false);
    private readonly SceltaRotazione _rotazione = new();
    private readonly BachecaObs _bacheca;
    private readonly LettoreBacheca _lettore;
    private long? _audio;
    private TimeSpan _adesso = TimeSpan.FromSeconds(10);

    public PresentazioneTest()
    {
        _coda = new CodaImmagini(_restituite.Add);
        _bacheca = new BachecaObs(_nome);
        _lettore = new LettoreBacheca(_nome);
    }

    public void Dispose()
    {
        _lettore.Dispose();
        _bacheca.Dispose();
        _nuova.Dispose();
    }

    private Presentazione Crea(BachecaObs? bacheca = null) => new(_coda.Prendi, () => _coda.ProssimoIstante,
        i => { lock (_restituite) _restituite.Add(i); }, _nuova, () => _audio, _rotazione, bacheca ?? _bacheca, () => _adesso);

    /// <summary>NV12 4×2 con luminosità 1–8 e colore (101, 102) (103, 104), come in RotazioneNv12Test.</summary>
    private static ImmaginePronta Immagine(long istante, RotazioneVideo rotazione = RotazioneVideo.Nessuna) =>
        new([1, 2, 3, 4, 5, 6, 7, 8, 101, 102, 103, 104], 4, 2, 4, istante, 0, rotazione);

    [Fact]
    public void SenzaAudioLaPiuRecenteVaSubitoInOndaPerOBSEPerLaFinestra()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(100));
        _coda.Aggiungi(Immagine(200));
        ulong prima = BachecaObs.OraNs();

        // Niente audio e niente altro in coda: si torna a guardare in tempo per l'eventuale ripetizione.
        Assert.Equal(Presentazione.IntervalloRipetizione, presentazione.Giro());

        var perObs = _lettore.UltimaImmagine()!;
        Assert.Equal((4, 2), (perObs.Larghezza, perObs.Altezza));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 101, 102, 103, 104 }, perObs.Dati);
        // A OBS va l'istante della messa in onda, non quello di cattura sull'iPhone.
        Assert.InRange(perObs.Istante, prima, BachecaObs.OraNs());

        var perFinestra = presentazione.PrendiPerFinestra()!;
        Assert.Equal(200, perFinestra.IstanteCatturaMicrosecondi);
        Assert.Equal(4 * 2 * 4, perFinestra.Bgra.Length);
        Assert.Null(presentazione.PrendiPerFinestra());

        Assert.Equal(1, presentazione.Presentate);
        Assert.Equal(1, presentazione.ScritteInBacheca);
        // Quella saltata dalla coda torna subito; quella andata in onda resta per le ripetizioni.
        Assert.Single(_restituite);
    }

    [Fact]
    public void AOBSArrivaGiaDrittaEAllaFinestraConLaRotazioneDaApplicare()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(100, RotazioneVideo.Gradi90));

        presentazione.Giro();

        var perObs = _lettore.UltimaImmagine()!;
        Assert.Equal((2, 4), (perObs.Larghezza, perObs.Altezza));
        Assert.Equal(new byte[] { 5, 1, 6, 2, 7, 3, 8, 4, 101, 102, 103, 104 }, perObs.Dati);
        Assert.Equal(RotazioneVideo.Gradi90, presentazione.PrendiPerFinestra()!.Rotazione);
    }

    [Fact]
    public void LaRotazioneManualeVinceSuQuellaDellIPhone()
    {
        var presentazione = Crea();
        _rotazione.RuotaManuale(); // da 0° a 90°, a mano
        _coda.Aggiungi(Immagine(100, RotazioneVideo.Nessuna));

        presentazione.Giro();

        Assert.Equal((2, 4), (_lettore.UltimaImmagine()!.Larghezza, _lettore.UltimaImmagine()!.Altezza));
    }

    [Fact]
    public void ConLAudioAspettaFinoAllIstanteDellaProssimaImmagine()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(90_000));
        _coda.Aggiungi(Immagine(130_000));
        _audio = 100_000;

        // Va in onda quella da 90 000; quella da 130 000 tocca tra 30 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(30), presentazione.Giro());
        Assert.Equal(90_000, presentazione.PrendiPerFinestra()!.IstanteCatturaMicrosecondi);
        Assert.Equal(10, presentazione.LeggiSincronia()!.Value.Mediana);

        _audio = 131_000;
        Assert.Equal(Presentazione.IntervalloRipetizione, presentazione.Giro());
        Assert.Equal(130_000, presentazione.PrendiPerFinestra()!.IstanteCatturaMicrosecondi);
        Assert.Equal(2, presentazione.ScritteInBacheca);
    }

    [Fact]
    public void LAttesaNonScendeSottoIlMillisecondo()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(100_000));
        _coda.Aggiungi(Immagine(100_200));
        _audio = 100_100;

        Assert.Equal(TimeSpan.FromMilliseconds(1), presentazione.Giro());
    }

    [Fact]
    public void ConLaFinestraRidottaAIconaSiScriveSoloPerOBS()
    {
        var presentazione = Crea();
        presentazione.FinestraVisibile = false;
        _coda.Aggiungi(Immagine(100));

        presentazione.Giro();

        Assert.Null(presentazione.PrendiPerFinestra());
        Assert.Equal(1, presentazione.ScritteInBacheca);
    }

    [Fact]
    public void SeLaFinestraRestaIndietroVedeSoloLUltima()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(100));
        presentazione.Giro();
        _coda.Aggiungi(Immagine(200));
        presentazione.Giro();

        Assert.Equal(200, presentazione.PrendiPerFinestra()!.IstanteCatturaMicrosecondi);
        Assert.Equal(2, presentazione.ScritteInBacheca);
    }

    [Fact]
    public void SenzaBachecaLaFinestraFunzionaLoStesso()
    {
        var presentazione = new Presentazione(_coda.Prendi, () => _coda.ProssimoIstante, _ => { }, _nuova, () => null, _rotazione, null);
        _coda.Aggiungi(Immagine(100));

        presentazione.Giro();

        Assert.NotNull(presentazione.PrendiPerFinestra());
        Assert.Equal(0, presentazione.ScritteInBacheca);
    }

    [Fact]
    public void ASchermoFermoSiRipeteLUltimaImmaginePerOBS()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(100));
        presentazione.Giro();
        ulong prima = _lettore.UltimaImmagine()!.Istante;

        // Dopo 10 ms è ancora presto; manca il resto dell'intervallo.
        _adesso += TimeSpan.FromMilliseconds(10);
        Assert.Equal(TimeSpan.FromMilliseconds(23), presentazione.Giro());
        Assert.Equal(1, presentazione.ScritteInBacheca);

        // Dopo 33 ms la stessa immagine, con un istante nuovo.
        _adesso += TimeSpan.FromMilliseconds(23);
        presentazione.Giro();
        var ripetuta = _lettore.UltimaImmagine()!;
        Assert.Equal((2, 1), (presentazione.ScritteInBacheca, presentazione.Ripetute));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 101, 102, 103, 104 }, ripetuta.Dati);
        Assert.True(ripetuta.Istante > prima);
        // Per la finestra non cambia niente: ha già quell'immagine.
        Assert.NotNull(presentazione.PrendiPerFinestra());
        Assert.Null(presentazione.PrendiPerFinestra());
        Assert.Equal(1, presentazione.Presentate);
    }

    [Fact]
    public void LaRotazioneAManoArrivaAOBSAncheASchermoFermo()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(100));
        presentazione.Giro();
        Assert.Equal(4, _lettore.UltimaImmagine()!.Larghezza);

        _rotazione.RuotaManuale(); // tasto O, con lo schermo dell'iPhone fermo
        _adesso += Presentazione.IntervalloRipetizione;
        presentazione.Giro();

        var girata = _lettore.UltimaImmagine()!;
        Assert.Equal((2, 4), (girata.Larghezza, girata.Altezza));
        Assert.Equal(new byte[] { 5, 1, 6, 2, 7, 3, 8, 4, 101, 102, 103, 104 }, girata.Dati);
    }

    [Fact]
    public void UnImmagineNuovaSostituisceQuellaDaRipetere()
    {
        var presentazione = Crea();
        _coda.Aggiungi(Immagine(100));
        presentazione.Giro();
        _coda.Aggiungi(Immagine(200));
        presentazione.Giro();

        // La prima torna nella riserva dei buffer solo ora, quando non serve più per le ripetizioni.
        Assert.Equal(100, _restituite.Single().IstanteCatturaMicrosecondi);
        Assert.Equal(0, presentazione.Ripetute);
    }

    [Fact]
    public void PrimaDellaPrimaImmagineNonSiRipeteNiente()
    {
        var presentazione = Crea();
        _adesso += TimeSpan.FromSeconds(1);

        Assert.Equal(TimeSpan.FromMilliseconds(50), presentazione.Giro());
        Assert.Equal(0, presentazione.ScritteInBacheca);
    }

    [Fact]
    public void SulSuoThreadVaInOndaAncheSenzaNessunoCheGuardi()
    {
        // Come con la finestra ridotta a icona: nessuno chiama Giro() a mano.
        var presentazione = Crea();
        presentazione.FinestraVisibile = false;
        using var stop = new CancellationTokenSource();
        Thread thread = presentazione.Avvia(stop.Token);

        _coda.Aggiungi(Immagine(100));
        _nuova.Set();
        Assert.True(_lettore.Nuovo.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.Equal(1ul, _lettore.ImmaginiScritte);

        stop.Cancel();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
