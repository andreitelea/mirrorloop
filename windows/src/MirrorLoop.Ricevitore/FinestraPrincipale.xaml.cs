using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

public partial class FinestraPrincipale : Window
{
    private const int Porta = 7450;
    private static readonly TimeSpan IntervalloStatistiche = TimeSpan.FromMilliseconds(500);

    private readonly CodaFrame _coda = new();
    private readonly StatisticheRicezione _statistiche = new();
    private readonly StatisticheRicezione _statisticheSchermo = new();
    private readonly StatisticheAudio _statisticheAudio = new();
    private readonly RiproduzioneAudio _riproduzioneAudio;
    private readonly SincroniaOrologi _sincronia = new();
    private readonly RegistroPacchetti _registro = new();
    private readonly RicezioneRete _ricezione;
    private readonly AnnuncioBonjour _annuncio;
    private readonly Riproduzione _riproduzione;
    private readonly BachecaObs? _bacheca;
    private readonly string? _motivoSenzaBacheca;
    private readonly Presentazione _presentazione;
    private readonly Timer _battito;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timerStatistiche;
    private readonly Stopwatch _dallUltimaLettura = Stopwatch.StartNew();
    private WriteableBitmap? _immagine;
    private RotazioneVideo _rotazione = RotazioneVideo.Nessuna;
    private readonly SceltaRotazione _sceltaRotazione = new();
    private long _keyframeTotali;
    // Quante volte WPF ci chiede un disegno: se sono ~60 al secondo anche con un monitor più veloce,
    // un video a 60 fps perde per forza qualche immagine.
    private long _aggiornamentiFinestra;
    private double _msDecompressione;
    private double _msConversione;
    private string _stato = "Avvio...";
    private long _mostratiTotali;
    // Contatori totali ogni mezzo secondo, per le medie degli ultimi 10 secondi: la media di mezzo
    // secondo salta troppo (le immagini arrivano a grappoli) e trae in inganno.
    private readonly Queue<(double Secondi, long InOnda, long Mostrati, long Bacheca)> _storico = new();
    private readonly Stopwatch _orologio = Stopwatch.StartNew();

    public FinestraPrincipale()
    {
        InitializeComponent();

        // La bacheca da cui legge la sorgente MirrorLoop in OBS (immagini e audio). Senza, la
        // finestra e le cuffie funzionano lo stesso.
        try
        {
            _bacheca = new BachecaObs();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            _motivoSenzaBacheca = ex.Message;
        }
        _riproduzioneAudio = new RiproduzioneAudio(_bacheca);

        _ricezione = new RicezioneRete(Porta, _coda, _statistiche, _statisticheAudio, _riproduzioneAudio, _sincronia, _registro, CaricaCertificato());
        // L'evento arriva da un thread in sottofondo: la finestra si può toccare solo dal suo thread.
        _ricezione.StatoCambiato += stato => Dispatcher.BeginInvoke(() => CambiaStato(stato));
        // Una trasmissione nuova: OBS deve ripartire pulito (istanti non confrontabili con prima).
        _ricezione.StatoCambiato += stato =>
        {
            if (stato.StartsWith("Collegato"))
                _bacheca?.NuovaGenerazione();
        };
        _riproduzioneAudio.EventoRiserva += evento => _registro.ScriviRiserva(evento, Protocollo.Protocollo.AdessoMicrosecondi());
        // L'iPhone trova il PC per nome, senza scrivere l'indirizzo.
        _annuncio = new AnnuncioBonjour(Environment.MachineName, Porta);

        _riproduzione = new Riproduzione(_coda);

        _presentazione = new Presentazione(_riproduzione, _riproduzioneAudio.IstanteInAscolto, _sceltaRotazione, _bacheca);
        // "Sono ancora qui" per OBS, anche quando non arrivano immagini (schermo fermo, iPhone scollegato).
        _battito = new Timer(_ => _bacheca?.Battito(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        // Ridotta a icona la finestra non si disegna: inutile convertire le immagini per lei.
        StateChanged += (_, _) => _presentazione.FinestraVisibile = WindowState != WindowState.Minimized;

        _timerStatistiche = new DispatcherTimer { Interval = IntervalloStatistiche };
        _timerStatistiche.Tick += (_, _) => AggiornaStatistiche();
        _timerStatistiche.Start();

        // Chiamato prima di ogni aggiornamento dello schermo (di solito 60 volte al secondo).
        CompositionTarget.Rendering += MostraUltimaImmagine;

        _ = _ricezione.EseguiAsync(_stop.Token);
        _riproduzione.Avvia(_stop.Token);
        _presentazione.Avvia(_stop.Token);
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

    /// <summary>
    /// Disegna l'ultima immagine andata in onda, se è nuova. La scelta (sincronia con l'audio) e la
    /// conversione le fa <see cref="Presentazione"/> sul suo thread: qui resta solo la copia.
    /// </summary>
    private void MostraUltimaImmagine(object? sender, EventArgs e)
    {
        _aggiornamentiFinestra++;
        Presentazione.ImmagineFinestra? immagine = _presentazione.PrendiPerFinestra();
        if (immagine is null)
            return;

        if (_immagine is null || _immagine.PixelWidth != immagine.Larghezza || _immagine.PixelHeight != immagine.Altezza)
        {
            _immagine = new WriteableBitmap(immagine.Larghezza, immagine.Altezza, 96, 96, PixelFormats.Bgr32, null);
            Video.Source = _immagine;
            TestoStato.Visibility = Visibility.Collapsed;
        }

        _immagine.WritePixels(new Int32Rect(0, 0, immagine.Larghezza, immagine.Altezza), immagine.Bgra, immagine.Larghezza * 4, 0);
        ApplicaRotazione(immagine.Rotazione);
        _mostratiTotali++;

        // Latenza "fino allo schermo": dal disegno nel mittente a qui. Manca solo l'ultimo tratto,
        // fino al prossimo aggiornamento del monitor (al massimo circa 16 ms a 60 Hz), non misurabile da qui.
        double latenzaMs = _ricezione.LatenzaMicrosecondi(immagine.IstanteCatturaMicrosecondi) / 1000.0;
        _statisticheSchermo.FrameRicevuto(false, 0, latenzaMs);
        _msDecompressione = immagine.MsDecompressione;
        _msConversione = immagine.MsConversione;

        _presentazione.RestituisciPerFinestra(immagine);
    }

    /// <summary>
    /// La rotazione la fa la scheda video al momento di disegnare: nessun lavoro sui pixel.
    /// LayoutTransform (non RenderTransform) perché anche l'impaginazione deve vedere l'immagine
    /// girata, così in orizzontale riempie la finestra in larghezza.
    /// </summary>
    private void ApplicaRotazione(RotazioneVideo rotazione)
    {
        if (rotazione == _rotazione)
            return;
        _rotazione = rotazione;
        var trasformazione = new RotateTransform(90 * (int)_rotazione);
        trasformazione.Freeze();
        Video.LayoutTransform = trasformazione;
    }

    private void AggiornaStatistiche()
    {
        var rete = _statistiche.Leggi();
        var schermo = _statisticheSchermo.Leggi();
        var (inOnda, mostrati, bacheca) = MedieUltimi10Secondi();
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
            $"in onda         {inOnda,6:F1} fps  (media 10 s; saltati per la sincronia {DescriviSaltati()} dall'inizio)\n" +
            $"finestra        {mostrati,6:F1} fps  (media 10 s; si aggiorna {_aggiornamentiFinestra / secondi:F0} volte/s)\n" +
            $"bacheca OBS     {DescriviBacheca(bacheca)}\n" +
            $"bitrate         {rete.Byte * 8 / secondi / 1_000_000,6:F2} Mbit/s\n" +
            $"latenza rete    {Latenza(rete)}\n" +
            $"latenza schermo {Latenza(schermo)}\n" +
            $"latenza audio   {DescriviLatenzaAudio()}\n" +
            $"orologi         {DescriviOrologi()}\n" +
            $"bonjour         {_annuncio.Stato}\n" +
            $"trasporto       {_ricezione.Trasporto ?? "—"}{(_ricezione.MotivoSenzaQuic is string m ? $"  (QUIC spento: {m})" : "")}\n" +
            $"decompressione  {_msDecompressione,6:F1} ms\n" +
            $"conversione     {_msConversione,6:F1} ms\n" +
            $"rotazione       {90 * (int)_rotazione,6}°  ({(_sceltaRotazione.Manuale ? $"manuale; dall'iPhone {90 * (int)_sceltaRotazione.Automatica}°" : "automatica")})\n" +
            $"keyframe        {_keyframeTotali,6} (totale)\n" +
            $"scartati        {_coda.Scartati,6} (totale)\n" +
            $"in attesa       {_riproduzione.InAttesa,6} immagini\n" +
            $"sincronia A/V   {DescriviSincronia()}\n" +
            $"errori          {_riproduzione.Errori,6} (totale)\n" +
            $"audio           {audio.Pacchetti / secondi,6:F1} pacchetti/s  {audio.Byte * 8 / secondi / 1000,5:F0} kbit/s\n" +
            $"audio ricevuto  {audio.MsAudio / secondi,6:F0} ms al secondo (~1000 = completo)\n" +
            $"formato audio   {audio.Formato}{(audio.Ignorati > 0 ? $"  ({audio.Ignorati} ignorati)" : "")}\n" +
            $"uscita audio    {_riproduzioneAudio.Stato}\n" +
            $"riserva audio   {_riproduzioneAudio.Riserva.TotalMilliseconds,6:F0} ms  (obiettivo {_riproduzioneAudio.Obiettivo.TotalMilliseconds:F0}, buchi {_riproduzioneAudio.Buchi}, tagli {_riproduzioneAudio.Tagli})\n" +
            $"velocità audio  {_riproduzioneAudio.Velocita,6:F3}×\n" +
            $"pausa audio     {audio.PausaMassimaMs,6:F0} ms  (massima {audio.PausaMassimaTotaleMs:F0} ms)\n" +
            $"registro        {DescriviRegistro()}\n" +
            "S: mostra/nascondi · R: registro su file · O: ruota di 90° · A: rotazione automatica";
        _aggiornamentiFinestra = 0;
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
    private string DescriviSincronia() =>
        _presentazione.LeggiSincronia() is var (mediana, massimo)
            ? $"{mediana,6:F0} ms video dietro l'audio (max {massimo:F0})"
            : "     — (niente audio: il video va in onda subito)";

    /// <summary>Immagini al secondo andate in onda, disegnate dalla finestra e scritte per OBS, negli ultimi 10 s.</summary>
    private (double InOnda, double Mostrati, double Bacheca) MedieUltimi10Secondi()
    {
        double adesso = _orologio.Elapsed.TotalSeconds;
        _storico.Enqueue((adesso, _presentazione.Presentate, _mostratiTotali, _presentazione.ScritteInBacheca));
        while (_storico.Count > 1 && adesso - _storico.Peek().Secondi > 10)
            _storico.Dequeue();
        var (secondi, inOnda, mostrati, bacheca) = _storico.Peek();
        double durata = adesso - secondi;
        if (durata <= 0)
            return (0, 0, 0);
        return ((_presentazione.Presentate - inOnda) / durata, (_mostratiTotali - mostrati) / durata,
            (_presentazione.ScritteInBacheca - bacheca) / durata);
    }

    /// <summary>Quota di immagini decompresse e mai andate in onda (superate da una più adatta all'audio).</summary>
    private string DescriviSaltati()
    {
        long saltati = _riproduzione.NonMostrate;
        long totale = saltati + _presentazione.Presentate;
        return totale == 0 ? "—" : $"{100.0 * saltati / totale:F1}% ({saltati})";
    }

    private string DescriviBacheca(double alSecondo)
    {
        if (_bacheca is null)
            return $"non disponibile: {_motivoSenzaBacheca}";
        string errori = _presentazione.ErroriBacheca > 0 ? $", {_presentazione.ErroriBacheca} errori" : "";
        string rotazione = _presentazione.MsRotazione > 0 ? $", rotazione {_presentazione.MsRotazione:F1} ms" : "";
        return $"{alSecondo,6:F1} immagini/s  ({_presentazione.ScritteInBacheca} scritte, {_presentazione.Ripetute} ripetute a schermo fermo{errori}{rotazione})";
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
        else if (e.Key == Key.O)
        {
            _sceltaRotazione.RuotaManuale();
            ApplicaRotazione(_sceltaRotazione.Effettiva);
        }
        else if (e.Key == Key.A)
        {
            _sceltaRotazione.TornaAutomatica();
            ApplicaRotazione(_sceltaRotazione.Effettiva);
        }
    }

    private string DescriviRegistro() =>
        _registro.Attivo
            ? $"ATTIVO ({_registro.Righe} righe) → {_registro.Percorso}"
            : _registro.Percorso is string ultimo ? $"spento (ultimo: {ultimo})" : "spento";

    protected override void OnClosing(CancelEventArgs e)
    {
        _stop.Cancel();
        _battito.Dispose();
        _annuncio.Dispose();
        _riproduzioneAudio.Dispose();
        _bacheca?.Dispose();
        _registro.Dispose();
        _timerStatistiche.Stop();
        CompositionTarget.Rendering -= MostraUltimaImmagine;
        base.OnClosing(e);
    }
}
