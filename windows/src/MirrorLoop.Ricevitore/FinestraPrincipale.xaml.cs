using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MirrorLoop.Ricevitore;

public partial class FinestraPrincipale : Window
{
    private const int Porta = 7450;
    private static readonly TimeSpan IntervalloStatistiche = TimeSpan.FromMilliseconds(500);

    private readonly CodaFrame _coda = new();
    private readonly StatisticheRicezione _statistiche = new();
    private readonly RicezioneRete _ricezione;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timerStatistiche;
    private readonly Stopwatch _dallUltimaLettura = Stopwatch.StartNew();
    private long _consegnati;
    private long _keyframeTotali;
    private string _stato = "Avvio...";

    public FinestraPrincipale()
    {
        InitializeComponent();

        _ricezione = new RicezioneRete(Porta, _coda, _statistiche);
        // L'evento arriva da un thread in sottofondo: la finestra si può toccare solo dal suo thread.
        _ricezione.StatoCambiato += stato => Dispatcher.BeginInvoke(() =>
        {
            _stato = stato;
            TestoStato.Text = stato;
        });

        _timerStatistiche = new DispatcherTimer { Interval = IntervalloStatistiche };
        _timerStatistiche.Tick += (_, _) => AggiornaStatistiche();
        _timerStatistiche.Start();

        _ = _ricezione.EseguiAsync(_stop.Token);
        _ = Task.Run(ConsumaCoda);
    }

    /// <summary>
    /// Segnaposto del decompressore (passo 3b): per ora prende i frame dalla coda e li conta,
    /// così la coda non si riempie e lo scarto entra in gioco solo se qualcosa rallenta davvero.
    /// </summary>
    private void ConsumaCoda()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (_coda.ProvaPrendi(TimeSpan.FromMilliseconds(200), out _))
                Interlocked.Increment(ref _consegnati);
        }
    }

    private void AggiornaStatistiche()
    {
        var s = _statistiche.Leggi();
        double secondi = _dallUltimaLettura.Elapsed.TotalSeconds;
        _dallUltimaLettura.Restart();
        // Un keyframe ogni 2 secondi: contarli per mezzo secondo darebbe quasi sempre 0.
        _keyframeTotali += s.Keyframe;

        string latenza = s.LatenzaMedianaMs is double mediana
            ? $"{mediana:F1} ms (max {s.LatenzaMassimaMs:F1})"
            : "—";

        TestoStatistiche.Text =
            $"{_stato}\n" +
            $"ricevuti     {s.Frame / secondi,6:F1} fps\n" +
            $"bitrate      {s.Byte * 8 / secondi / 1_000_000,6:F2} Mbit/s\n" +
            $"keyframe     {_keyframeTotali,6} (totale)\n" +
            $"latenza rete {latenza}\n" +
            $"scartati     {_coda.Scartati,6} (totale)\n" +
            $"consegnati   {Interlocked.Read(ref _consegnati),6} (totale)\n" +
            "S: mostra/nascondi";
    }

    private void SuTastoPremuto(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S)
        {
            RiquadroStatistiche.Visibility = RiquadroStatistiche.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _stop.Cancel();
        _timerStatistiche.Stop();
        base.OnClosing(e);
    }
}
