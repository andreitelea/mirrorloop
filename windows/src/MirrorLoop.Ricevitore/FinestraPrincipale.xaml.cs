using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MirrorLoop.Ricevitore;

public partial class FinestraPrincipale : Window
{
    private const int Porta = 7450;
    private static readonly TimeSpan IntervalloStatistiche = TimeSpan.FromMilliseconds(500);

    private readonly CodaFrame _coda = new();
    private readonly StatisticheRicezione _statistiche = new();
    private readonly StatisticheRicezione _statisticheSchermo = new();
    private readonly StatisticheAudio _statisticheAudio = new();
    private readonly RiproduzioneAudio _riproduzioneAudio = new();
    private readonly SincroniaOrologi _sincronia = new();
    private readonly RegistroPacchetti _registro = new();
    private readonly RicezioneRete _ricezione;
    private readonly Riproduzione _riproduzione;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timerStatistiche;
    private readonly Stopwatch _dallUltimaLettura = Stopwatch.StartNew();
    private WriteableBitmap? _immagine;
    private long _keyframeTotali;
    private double _msDecompressione;
    private double _msConversione;
    private string _stato = "Avvio...";
    private readonly List<double> _scartiAudioVideoMs = [];

    public FinestraPrincipale()
    {
        InitializeComponent();

        _ricezione = new RicezioneRete(Porta, _coda, _statistiche, _statisticheAudio, _riproduzioneAudio, _sincronia, _registro, CaricaCertificato());
        // L'evento arriva da un thread in sottofondo: la finestra si può toccare solo dal suo thread.
        _ricezione.StatoCambiato += stato => Dispatcher.BeginInvoke(() => CambiaStato(stato));

        _riproduzione = new Riproduzione(_coda);

        _timerStatistiche = new DispatcherTimer { Interval = IntervalloStatistiche };
        _timerStatistiche.Tick += (_, _) => AggiornaStatistiche();
        _timerStatistiche.Start();

        // Chiamato prima di ogni aggiornamento dello schermo (di solito 60 volte al secondo).
        CompositionTarget.Rendering += MostraUltimaImmagine;

        _ = _ricezione.EseguiAsync(_stop.Token);
        _riproduzione.Avvia(_stop.Token);
    }

    private void CambiaStato(string stato)
    {
        _stato = stato;
        TestoStato.Text = stato;
        // Senza mittente non c'è video da mostrare: si torna alla scritta di stato.
        if (!stato.StartsWith("Collegato"))
        {
            Video.Source = null;
            _immagine = null;
            TestoStato.Visibility = Visibility.Visible;
        }
    }

    private void MostraUltimaImmagine(object? sender, EventArgs e)
    {
        long? istanteAudio = _riproduzioneAudio.IstanteInAscolto();
        ImmaginePronta? immagine = _riproduzione.Prendi(istanteAudio);
        if (immagine is null)
            return;

        // Quanto l'immagine mostrata è dietro all'audio che si sente: idealmente tra 0 e la durata
        // di un fotogramma (33 ms a 30 fps). Senza audio non c'è niente da misurare.
        if (istanteAudio is long audio)
            _scartiAudioVideoMs.Add((audio - immagine.IstanteCatturaMicrosecondi) / 1000.0);

        if (_immagine is null || _immagine.PixelWidth != immagine.Larghezza || _immagine.PixelHeight != immagine.Altezza)
        {
            _immagine = new WriteableBitmap(immagine.Larghezza, immagine.Altezza, 96, 96, PixelFormats.Bgr32, null);
            Video.Source = _immagine;
            TestoStato.Visibility = Visibility.Collapsed;
        }

        _immagine.WritePixels(new Int32Rect(0, 0, immagine.Larghezza, immagine.Altezza), immagine.Bgra, immagine.Larghezza * 4, 0);

        // Latenza "fino allo schermo": dal disegno nel mittente a qui. Manca solo l'ultimo tratto,
        // fino al prossimo aggiornamento del monitor (al massimo circa 16 ms a 60 Hz), non misurabile da qui.
        double latenzaMs = _ricezione.LatenzaMicrosecondi(immagine.IstanteCatturaMicrosecondi) / 1000.0;
        _statisticheSchermo.FrameRicevuto(false, 0, latenzaMs);
        _msDecompressione = immagine.MsDecompressione;
        _msConversione = immagine.MsConversione;

        _riproduzione.Restituisci(immagine);
    }

    private void AggiornaStatistiche()
    {
        var rete = _statistiche.Leggi();
        var schermo = _statisticheSchermo.Leggi();
        var audio = _statisticheAudio.Leggi();
        double secondi = _dallUltimaLettura.Elapsed.TotalSeconds;
        _dallUltimaLettura.Restart();
        // Un keyframe ogni 2 secondi: contarli per mezzo secondo darebbe quasi sempre 0.
        _keyframeTotali += rete.Keyframe;

        static string Latenza(StatisticheRicezione.Istantanea s) => s.LatenzaMedianaMs is double mediana
            ? $"{mediana,6:F1} ms (max {s.LatenzaMassimaMs:F1})"
            : "     —";

        TestoStatistiche.Text =
            $"{_stato}\n" +
            $"ricevuti        {rete.Frame / secondi,6:F1} fps\n" +
            $"mostrati        {schermo.Frame / secondi,6:F1} fps\n" +
            $"bitrate         {rete.Byte * 8 / secondi / 1_000_000,6:F2} Mbit/s\n" +
            $"latenza rete    {Latenza(rete)}\n" +
            $"latenza schermo {Latenza(schermo)}\n" +
            $"latenza audio   {DescriviLatenzaAudio()}\n" +
            $"orologi         {DescriviOrologi()}\n" +
            $"trasporto       {_ricezione.Trasporto ?? "—"}{(_ricezione.MotivoSenzaQuic is string m ? $"  (QUIC spento: {m})" : "")}\n" +
            $"decompressione  {_msDecompressione,6:F1} ms\n" +
            $"conversione     {_msConversione,6:F1} ms\n" +
            $"keyframe        {_keyframeTotali,6} (totale)\n" +
            $"scartati        {_coda.Scartati,6} (totale)\n" +
            $"non mostrati    {_riproduzione.NonMostrate,6} (totale)\n" +
            $"in attesa       {_riproduzione.InAttesa,6} immagini\n" +
            $"sincronia A/V   {DescriviSincronia()}\n" +
            $"errori          {_riproduzione.Errori,6} (totale)\n" +
            $"audio           {audio.Pacchetti / secondi,6:F1} pacchetti/s  {audio.Byte * 8 / secondi / 1000,5:F0} kbit/s\n" +
            $"audio ricevuto  {audio.MsAudio / secondi,6:F0} ms al secondo (~1000 = completo)\n" +
            $"formato audio   {audio.Formato}{(audio.Ignorati > 0 ? $"  ({audio.Ignorati} ignorati)" : "")}\n" +
            $"uscita audio    {_riproduzioneAudio.Stato}\n" +
            $"riserva audio   {_riproduzioneAudio.Riserva.TotalMilliseconds,6:F0} ms  (buchi {_riproduzioneAudio.Buchi}, tagli {_riproduzioneAudio.Tagli})\n" +
            $"velocità audio  {_riproduzioneAudio.Velocita,6:F3}×\n" +
            $"pausa audio     {audio.PausaMassimaMs,6:F0} ms  (massima {audio.PausaMassimaTotaleMs:F0} ms)\n" +
            $"registro        {DescriviRegistro()}\n" +
            "S: mostra/nascondi · R: registro su file";
    }

    /// <summary>Senza certificato il ricevitore funziona lo stesso, solo in TCP.</summary>
    private static System.Security.Cryptography.X509Certificates.X509Certificate2? CaricaCertificato()
    {
        try
        {
            return CertificatoRicevitore.CaricaOCrea();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Dalla cattura sull'iPhone al suono nelle cuffie (stimato con la latenza dell'uscita).</summary>
    private string DescriviLatenzaAudio() =>
        _riproduzioneAudio.IstanteInAscolto() is long istante
            ? $"{_ricezione.LatenzaMicrosecondi(istante) / 1000.0,6:F1} ms"
            : "     —";

    /// <summary>
    /// Lo scarto tra l'orologio dell'iPhone e quello del PC e il tempo del giro ping-pong migliore.
    /// Senza sincronia (iPhone con una versione che non risponde ai ping) le latenze sono falsate.
    /// </summary>
    private string DescriviOrologi() =>
        _sincronia.Migliore is SincroniaOrologi.Misura m
            ? $"scarto {m.ScartoMicrosecondi / 1000.0:+0.0;-0.0} ms · andata/ritorno {m.AndataRitornoMicrosecondi / 1000.0:F1} ms"
            : "non sincronizzati (le latenze qui sopra non sono affidabili)";

    /// <summary>Mediana e massimo dello scarto audio-video dall'ultima lettura, poi riparte.</summary>
    private string DescriviSincronia()
    {
        if (_scartiAudioVideoMs.Count == 0)
            return "     — (niente audio: il video si mostra subito)";
        var ordinati = _scartiAudioVideoMs.Order().ToArray();
        _scartiAudioVideoMs.Clear();
        return $"{ordinati[ordinati.Length / 2],6:F0} ms video dietro l'audio (max {ordinati[^1]:F0})";
    }

    private void SuTastoPremuto(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S)
        {
            RiquadroStatistiche.Visibility = RiquadroStatistiche.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        else if (e.Key == Key.R)
        {
            if (_registro.Attivo)
                _registro.Ferma();
            else
                _registro.Avvia();
        }
    }

    private string DescriviRegistro() =>
        _registro.Attivo
            ? $"ATTIVO ({_registro.Righe} righe) → {_registro.Percorso}"
            : _registro.Percorso is string ultimo ? $"spento (ultimo: {ultimo})" : "spento";

    protected override void OnClosing(CancelEventArgs e)
    {
        _stop.Cancel();
        _riproduzioneAudio.Dispose();
        _registro.Dispose();
        _timerStatistiche.Stop();
        CompositionTarget.Rendering -= MostraUltimaImmagine;
        base.OnClosing(e);
    }
}
