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
    private readonly TelefoniAbbinati _telefoni = new();
    // La richiesta di abbinamento mostrata nel riquadro, se c'è, e quando scade.
    private TaskCompletionSource<bool>? _consenso;
    private DateTime _scadenzaConsenso;
    private readonly DispatcherTimer _contoAllaRovescia;
    // Ogni pochi secondi si rilegge il tipo della rete di casa: chi la imposta come Privata vede
    // sparire l'avviso senza riavviare.
    private readonly DispatcherTimer _controlloRete = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Impostazioni _impostazioni = new();
    // Null solo mentre si cambia nome (il vecchio annuncio si toglie, il nuovo non c'è ancora).
    private AnnuncioBonjour? _annuncio;
    private bool _cambioNomeInCorso;
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
    private string _stato = Testi.Avvio;
    private long _mostratiTotali;
    // Contatori totali ogni mezzo secondo, per le medie degli ultimi 10 secondi: la media di mezzo
    // secondo salta troppo (le immagini arrivano a grappoli) e trae in inganno.
    private readonly Queue<(double Secondi, long InOnda, long Mostrati, long Bacheca)> _storico = new();
    private readonly Stopwatch _orologio = Stopwatch.StartNew();

    public FinestraPrincipale()
    {
        InitializeComponent();
        ApplicaTesti();

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

        _ricezione = new RicezioneRete(Porta, _coda, _statistiche, _statisticheAudio, _riproduzioneAudio, _sincronia, _registro, CaricaCertificato(),
            _telefoni, ChiediConsensoAsync);
        _contoAllaRovescia = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _contoAllaRovescia.Tick += (_, _) => AggiornaScadenza();
        // L'evento arriva da un thread in sottofondo: la finestra si può toccare solo dal suo thread.
        _ricezione.StatoCambiato += stato => Dispatcher.BeginInvoke(() => CambiaStato(stato));
        // Una trasmissione nuova: OBS deve ripartire pulito (istanti non confrontabili con prima).
        _ricezione.StatoCambiato += stato =>
        {
            if (stato.Collegato)
                _bacheca?.NuovaGenerazione();
        };
        _ricezione.AbbinamentoRifiutato += rifiuto => Dispatcher.BeginInvoke(() =>
        {
            TestoRifiuto.Text = string.Format(Testi.UltimoRifiutato, rifiuto.Riga);
            TestoRifiuto.Visibility = Visibility.Visible;
        });
        _riproduzioneAudio.EventoRiserva +=evento => _registro.ScriviRiserva(evento, Protocollo.Protocollo.AdessoMicrosecondi());
        // L'iPhone trova il PC per nome, senza scrivere l'indirizzo.
        _annuncio = new AnnuncioBonjour(_impostazioni.NomePC, Porta);
        MostraNomePC();
        MostraStatistiche(_impostazioni.StatisticheVisibili);

        _riproduzione = new Riproduzione(_coda);

        _presentazione = new Presentazione(_riproduzione, _riproduzioneAudio.IstanteInAscolto, _sceltaRotazione, _bacheca);
        // "Sono ancora qui" per OBS, anche quando non arrivano immagini (schermo fermo, iPhone scollegato).
        _battito = new Timer(_ => _bacheca?.Battito(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        // Ridotta a icona la finestra non si disegna: inutile convertire le immagini per lei.
        StateChanged += (_, _) => _presentazione.FinestraVisibile = WindowState != WindowState.Minimized;

        _timerStatistiche = new DispatcherTimer { Interval = IntervalloStatistiche };
        _timerStatistiche.Tick += (_, _) => AggiornaStatistiche();
        _timerStatistiche.Start();
        _controlloRete.Tick += async (_, _) => await ControllaReteAsync();
        _controlloRete.Start();
        _ = ControllaReteAsync();

        // Chiamato prima di ogni aggiornamento dello schermo (di solito 60 volte al secondo).
        CompositionTarget.Rendering += MostraUltimaImmagine;

        _ = _ricezione.EseguiAsync(_stop.Token);
        _riproduzione.Avvia(_stop.Token);
        _presentazione.Avvia(_stop.Token);
    }

    /// <summary>I testi fissi della finestra, nella lingua di Windows (Lingue\Testi*.resx).</summary>
    private void ApplicaTesti()
    {
        TestoStato.Text = Testi.Avvio;
        TestoTastiInBasso.Text = Testi.TastiInBasso;
        TestoAiutoTitolo.Text = Testi.AiutoTitolo;
        TestoAiutoF1.Text = Testi.AiutoF1;
        TestoAiutoS.Text = Testi.AiutoS;
        TestoAiutoO.Text = Testi.AiutoO;
        TestoAiutoA.Text = Testi.AiutoA;
        TestoAiutoN.Text = Testi.AiutoN;
        TestoAiutoD.Text = Testi.AiutoD;
        TestoAiutoEsc.Text = Testi.AiutoEsc;
        TestoAbbinamentoTitolo.Text = Testi.AbbinamentoTitolo;
        TestoAbbinamentoIstruzioni.Text = Testi.AbbinamentoIstruzioni;
        PulsanteConsenti.Content = Testi.Consenti;
        PulsanteRifiuta.Content = Testi.Rifiuta;
    }

    private void CambiaStato(StatoRicezione stato)
    {
        _stato = stato.Descrizione;
        TestoStato.Text = stato.Descrizione;
        // Un iPhone collegato: il rifiuto di prima non interessa più (resta nelle statistiche).
        if (stato.Collegato)
            TestoRifiuto.Visibility = Visibility.Collapsed;
        // Senza mittente non c'è video da mostrare: si torna alla scritta di stato.
        else
        {
            Video.Source = null;
            _immagine = null;
            PannelloAttesa.Visibility = Visibility.Visible;
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
            PannelloAttesa.Visibility = Visibility.Collapsed;
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
        // Etichette allineate in colonna (carattere a larghezza fissa): al massimo 15 caratteri in ogni lingua.
        static string Riga(string etichetta, string valore) => $"{etichetta,-15} {valore}\n";
        string rotazione = _sceltaRotazione.Manuale
            ? string.Format(Testi.ValRotazioneManuale, 90 * (int)_rotazione, 90 * (int)_sceltaRotazione.Automatica)
            : string.Format(Testi.ValRotazioneAutomatica, 90 * (int)_rotazione);

        TestoStatistiche.Text =
            $"{_stato}\n" +
            Riga(Testi.StatRicevuti, $"{rete.Frame / secondi,6:F1} fps") +
            Riga(Testi.StatInOnda, string.Format(Testi.ValInOnda, inOnda, DescriviSaltati())) +
            Riga(Testi.StatFinestra, string.Format(Testi.ValFinestra, mostrati, _aggiornamentiFinestra / secondi)) +
            Riga(Testi.StatBacheca, DescriviBacheca(bacheca)) +
            Riga(Testi.StatBitrate, $"{rete.Byte * 8 / secondi / 1_000_000,6:F2} Mbit/s") +
            Riga(Testi.StatLatenzaRete, Latenza(rete)) +
            Riga(Testi.StatLatenzaSchermo, Latenza(schermo)) +
            Riga(Testi.StatLatenzaAudio, DescriviLatenzaAudio()) +
            Riga(Testi.StatOrologi, DescriviOrologi()) +
            Riga(Testi.StatBonjour, _annuncio?.Stato ?? Testi.CambioNomeInCorso) +
            Riga(Testi.StatTrasporto, (_ricezione.Trasporto ?? "—")
                + (_ricezione.MotivoSenzaQuic is string m ? string.Format(Testi.ValTrasportoImpossibile, m) : "")) +
            Riga(Testi.StatAbbinati, $"{_telefoni.Numero,6}") +
            Riga(Testi.StatUltimoRifiuto, _ricezione.UltimoRifiuto?.Riga ?? "     —") +
            Riga(Testi.StatDecompressione, $"{_msDecompressione,6:F1} ms") +
            Riga(Testi.StatConversione, $"{_msConversione,6:F1} ms") +
            Riga(Testi.StatRotazione, rotazione) +
            Riga(Testi.StatKeyframe, string.Format(Testi.ValTotale, _keyframeTotali)) +
            Riga(Testi.StatScartati, string.Format(Testi.ValTotale, _coda.Scartati)) +
            Riga(Testi.StatInAttesa, string.Format(Testi.ValInAttesa, _riproduzione.InAttesa)) +
            Riga(Testi.StatSincronia, DescriviSincronia()) +
            Riga(Testi.StatErrori, string.Format(Testi.ValTotale, _riproduzione.Errori)) +
            Riga(Testi.StatAudio, string.Format(Testi.ValAudio, audio.Pacchetti / secondi, audio.Byte * 8 / secondi / 1000)) +
            Riga(Testi.StatAudioRicevuto, string.Format(Testi.ValAudioRicevuto, audio.MsAudio / secondi)) +
            Riga(Testi.StatFormatoAudio, audio.Formato + (audio.Ignorati > 0 ? string.Format(Testi.ValIgnorati, audio.Ignorati) : "")) +
            Riga(Testi.StatUscitaAudio, _riproduzioneAudio.Stato) +
            Riga(Testi.StatRiserva, string.Format(Testi.ValRiserva, _riproduzioneAudio.Riserva.TotalMilliseconds,
                _riproduzioneAudio.Obiettivo.TotalMilliseconds, _riproduzioneAudio.Buchi, _riproduzioneAudio.Tagli)) +
            Riga(Testi.StatVelocita, $"{_riproduzioneAudio.Velocita,6:F3}×") +
            Riga(Testi.StatPausa, string.Format(Testi.ValPausa, audio.PausaMassimaMs, audio.PausaMassimaTotaleMs)) +
            Riga(Testi.StatRegistro, $"{DescriviRegistro()}  (Ctrl+R)") +
            Testi.StatTasti;
        _aggiornamentiFinestra = 0;
    }

    /// <summary>Senza certificato non si può ricevere: la finestra lo dice invece di chiudersi.</summary>
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
            ? string.Format(Testi.ValOrologi, m.ScartoMicrosecondi / 1000.0, m.AndataRitornoMicrosecondi / 1000.0)
            : Testi.OrologiNonSincronizzati;

    /// <summary>Mediana e massimo dello scarto audio-video dall'ultima lettura, poi riparte.</summary>
    private string DescriviSincronia() =>
        _presentazione.LeggiSincronia() is var (mediana, massimo)
            ? string.Format(Testi.ValSincronia, mediana, massimo)
            : Testi.SincroniaSenzaAudio;

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
            return string.Format(Testi.BachecaNonDisponibile, _motivoSenzaBacheca);
        string errori = _presentazione.ErroriBacheca > 0 ? string.Format(Testi.BachecaErrori, _presentazione.ErroriBacheca) : "";
        string rotazione = _presentazione.MsRotazione > 0 ? string.Format(Testi.BachecaRotazione, _presentazione.MsRotazione) : "";
        return string.Format(Testi.ValBacheca, alSecondo, _presentazione.ScritteInBacheca, _presentazione.Ripetute, errori, rotazione);
    }

    private void SuTastoPremuto(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            RiquadroAiuto.Visibility = RiquadroAiuto.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (e.Key == Key.S)
        {
            bool visibili = RiquadroStatistiche.Visibility != Visibility.Visible;
            MostraStatistiche(visibili);
            try
            {
                _impostazioni.CambiaStatisticheVisibili(visibili);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Solo una preferenza: al prossimo avvio le statistiche saranno com'erano, niente di grave.
            }
        }
        else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Con Ctrl: un registro avviato per sbaglio, a statistiche chiuse, crescerebbe senza che nessuno lo sappia.
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
        else if (e.Key == Key.D)
        {
            DimenticaTelefoni();
        }
        else if (e.Key == Key.N)
        {
            _ = CambiaNomePCAsync();
        }
        else if (e.Key == Key.Escape)
        {
            if (_consenso is not null)
                _consenso.TrySetResult(false);
            else
                RiquadroAiuto.Visibility = Visibility.Collapsed;
        }
    }

    private void MostraStatistiche(bool visibili) =>
        RiquadroStatistiche.Visibility = visibili ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Mostra o nasconde l'avviso di rete pubblica (la lettura, via COM, fuori dal thread della finestra).</summary>
    private async Task ControllaReteAsync()
    {
        string? avviso = CategoriaRete.Avviso(await Task.Run(CategoriaRete.DellaReteDiCasa));
        TestoAvvisoRete.Text = avviso ?? "";
        RiquadroAvvisoRete.Visibility = avviso is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Nome del PC ----

    private void MostraNomePC() =>
        TestoNomePC.Text = string.Format(Testi.SceltaSullIphone, _impostazioni.NomePC);

    /// <summary>
    /// Tasto N: chiede il nome nuovo e rifà l'annuncio Bonjour. Gli abbinamenti restano validi;
    /// un iPhone collegato continua a trasmettere, al prossimo collegamento va scelto il nome nuovo.
    /// </summary>
    private async Task CambiaNomePCAsync()
    {
        if (_cambioNomeInCorso)
            return;
        var finestra = new FinestraNome(_impostazioni.NomePC, _impostazioni.NomePCPredefinito) { Owner = this };
        if (finestra.ShowDialog() != true)
            return;
        try
        {
            _impostazioni.CambiaNomePC(finestra.NomeScelto);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, string.Format(Testi.ErroreNome, ex.Message), "MirrorLoop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MostraNomePC();
        if (_annuncio?.Nome == _impostazioni.NomePC)
            return;

        _cambioNomeInCorso = true;
        try
        {
            AnnuncioBonjour? vecchio = _annuncio;
            _annuncio = null;
            // Togliere l'annuncio aspetta la conferma di Windows (fino a 2 s): non sul thread della finestra.
            if (vecchio is not null)
                await Task.Run(vecchio.Dispose);
            if (!_stop.IsCancellationRequested)
                _annuncio = new AnnuncioBonjour(_impostazioni.NomePC, Porta);
        }
        finally
        {
            _cambioNomeInCorso = false;
        }
    }

    // ---- Abbinamento ----

    /// <summary>
    /// Chiamata dalla rete (thread in sottofondo) per un iPhone nuovo: mostra il riquadro e aspetta
    /// Consenti, Rifiuta o la scadenza (il token si annulla anche se il telefono sparisce).
    /// </summary>
    private async Task<bool> ChiediConsensoAsync(RichiestaAbbinamento richiesta, CancellationToken ct)
    {
        var risposta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // BeginInvoke e non Invoke: alla chiusura della finestra non deve lanciare eccezioni alla rete.
        _ = Dispatcher.BeginInvoke(() => MostraRichiesta(richiesta, risposta));
        using (ct.Register(() => risposta.TrySetResult(false)))
        {
            try
            {
                return await risposta.Task;
            }
            finally
            {
                _ = Dispatcher.BeginInvoke(() => NascondiRichiesta(risposta));
            }
        }
    }

    private void MostraRichiesta(RichiestaAbbinamento richiesta, TaskCompletionSource<bool> risposta)
    {
        // Già risolta (scaduta o telefono sparito) prima ancora di essere mostrata.
        if (risposta.Task.IsCompleted)
            return;
        _consenso = risposta;
        NomeAbbinamento.Text = richiesta.Nome;
        CodiceAbbinamento.Text = richiesta.CodiceLeggibile;
        _scadenzaConsenso = DateTime.UtcNow + _ricezione.AttesaConsenso;
        AggiornaScadenza();
        // Stesso posto al centro della finestra: l'aiuto lascia il posto alla richiesta.
        RiquadroAiuto.Visibility = Visibility.Collapsed;
        RiquadroAbbinamento.Visibility = Visibility.Visible;
        _contoAllaRovescia.Start();
        // Chi sta al PC deve vederla: se la finestra è ridotta a icona la si riapre. Se Windows non
        // permette di portarla davanti, fa lampeggiare l'icona sulla barra.
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void NascondiRichiesta(TaskCompletionSource<bool> risposta)
    {
        if (_consenso != risposta)
            return;
        _consenso = null;
        RiquadroAbbinamento.Visibility = Visibility.Collapsed;
        _contoAllaRovescia.Stop();
    }

    private void AggiornaScadenza()
    {
        int secondi = Math.Max(0, (int)Math.Ceiling((_scadenzaConsenso - DateTime.UtcNow).TotalSeconds));
        ScadenzaAbbinamento.Text = string.Format(Testi.ScadeTra, secondi);
    }

    private void SuConsenti(object sender, RoutedEventArgs e) => _consenso?.TrySetResult(true);

    private void SuRifiuta(object sender, RoutedEventArgs e) => _consenso?.TrySetResult(false);

    /// <summary>Tasto D: dopo una conferma, ogni iPhone dovrà di nuovo abbinarsi con il codice.</summary>
    private void DimenticaTelefoni()
    {
        int numero = _telefoni.Numero;
        if (numero == 0)
        {
            MessageBox.Show(this, Testi.NessunIphoneAbbinato, "MirrorLoop");
            return;
        }
        string domanda = numero == 1 ? Testi.DimenticaUno : string.Format(Testi.DimenticaMolti, numero);
        var scelta = MessageBox.Show(this, domanda, "MirrorLoop", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (scelta != MessageBoxResult.Yes)
            return;
        try
        {
            _telefoni.DimenticaTutti();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, string.Format(Testi.ErroreElenco, ex.Message), "MirrorLoop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string DescriviRegistro() =>
        _registro.Attivo
            ? string.Format(Testi.RegistroAttivo, _registro.Righe, _registro.Percorso)
            : _registro.Percorso is string ultimo ? string.Format(Testi.RegistroSpentoUltimo, ultimo) : Testi.RegistroSpento;

    protected override void OnClosing(CancelEventArgs e)
    {
        _stop.Cancel();
        _battito.Dispose();
        _annuncio?.Dispose();
        _riproduzioneAudio.Dispose();
        _bacheca?.Dispose();
        _registro.Dispose();
        _timerStatistiche.Stop();
        _controlloRete.Stop();
        CompositionTarget.Rendering -= MostraUltimaImmagine;
        base.OnClosing(e);
    }
}
