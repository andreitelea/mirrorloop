using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Decide quale immagine va "in onda" adesso e la consegna a OBS (bacheca) e alla finestra.
///
/// Lavora su un thread tutto suo, non sul ritmo della finestra: quando la finestra è ridotta a icona
/// Windows smette di disegnarla, ma OBS deve continuare a ricevere le immagini. La scelta è la stessa
/// di prima ("l'audio comanda", vedi <see cref="CodaImmagini"/>): un'immagine va in onda quando
/// l'audio che si sente ha raggiunto il suo istante di cattura.
///
/// A OBS l'immagine arriva già dritta e con l'istante "adesso" nel tempo di OBS: è già sincronizzata
/// con l'audio, e OBS deve solo mostrarla subito. La finestra la riceve in BGRA, e la gira da sé.
/// </summary>
public sealed partial class Presentazione
{
    /// <summary>Un'immagine convertita per la finestra.</summary>
    public sealed record ImmagineFinestra(byte[] Bgra, int Larghezza, int Altezza, RotazioneVideo Rotazione,
        long IstanteCatturaMicrosecondi, double MsDecompressione, double MsConversione);

    // Senza immagini in arrivo si ricontrolla comunque ogni tanto (per esempio l'audio che riparte).
    private static readonly TimeSpan AttesaMassima = TimeSpan.FromMilliseconds(50);

    private readonly Func<long?, ImmaginePronta?> _prendi;
    private readonly Func<long?> _prossimoIstante;
    private readonly Action<ImmaginePronta> _restituisci;
    private readonly WaitHandle _nuovaImmagine;
    private readonly Func<long?> _istanteAudio;
    private readonly SceltaRotazione _rotazione;
    private readonly BachecaObs? _bacheca;
    private readonly ConcurrentBag<byte[]> _bgraLiberi = [];
    private readonly object _bloccoScarti = new();
    private readonly List<double> _scartiAudioVideoMs = [];
    private byte[] _girata = [];
    private ImmagineFinestra? _perFinestra;
    private volatile bool _finestraVisibile = true;
    private long _presentate;
    private long _scritteInBacheca;
    private long _erroriBacheca;
    private double _msRotazione;

    public Presentazione(Riproduzione riproduzione, Func<long?> istanteAudio, SceltaRotazione rotazione, BachecaObs? bacheca)
        : this(riproduzione.Prendi, () => riproduzione.ProssimoIstante, riproduzione.Restituisci, riproduzione.NuovaImmagine,
            istanteAudio, rotazione, bacheca)
    {
    }

    /// <summary>Con le funzioni della coda separate, per i test.</summary>
    internal Presentazione(Func<long?, ImmaginePronta?> prendi, Func<long?> prossimoIstante, Action<ImmaginePronta> restituisci,
        WaitHandle nuovaImmagine, Func<long?> istanteAudio, SceltaRotazione rotazione, BachecaObs? bacheca)
    {
        _prendi = prendi;
        _prossimoIstante = prossimoIstante;
        _restituisci = restituisci;
        _nuovaImmagine = nuovaImmagine;
        _istanteAudio = istanteAudio;
        _rotazione = rotazione;
        _bacheca = bacheca;
    }

    /// <summary>Se la finestra è ridotta a icona è inutile convertire le immagini per lei.</summary>
    public bool FinestraVisibile
    {
        get => _finestraVisibile;
        set => _finestraVisibile = value;
    }

    /// <summary>Immagini andate in onda dall'avvio.</summary>
    public long Presentate => Interlocked.Read(ref _presentate);

    /// <summary>Immagini scritte nella bacheca per OBS dall'avvio.</summary>
    public long ScritteInBacheca => Interlocked.Read(ref _scritteInBacheca);

    public long ErroriBacheca => Interlocked.Read(ref _erroriBacheca);

    /// <summary>Tempo dell'ultima rotazione per OBS (0 se l'immagine era già dritta).</summary>
    public double MsRotazione => Volatile.Read(ref _msRotazione);

    public Thread Avvia(CancellationToken stop)
    {
        var thread = new Thread(() => Esegui(stop)) { IsBackground = true, Name = "Presentazione", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        return thread;
    }

    /// <summary>Per la finestra: l'ultima immagine andata in onda, se è nuova.</summary>
    public ImmagineFinestra? PrendiPerFinestra() => Interlocked.Exchange(ref _perFinestra, null);

    /// <summary>Per la finestra: restituisce il buffer di un'immagine già disegnata, così si riusa.</summary>
    public void RestituisciPerFinestra(ImmagineFinestra immagine) => _bgraLiberi.Add(immagine.Bgra);

    /// <summary>Di quanti ms il video in onda è dietro all'audio che si sente: mediana e massimo dall'ultima lettura.</summary>
    public (double Mediana, double Massimo)? LeggiSincronia()
    {
        lock (_bloccoScarti)
        {
            if (_scartiAudioVideoMs.Count == 0)
                return null;
            var ordinati = _scartiAudioVideoMs.Order().ToArray();
            _scartiAudioVideoMs.Clear();
            return (ordinati[ordinati.Length / 2], ordinati[^1]);
        }
    }

    private void Esegui(CancellationToken stop)
    {
        // Le attese di Windows hanno di norma una precisione di ~15 ms: troppo poco per immagini
        // che arrivano ogni 16. Si chiede la precisione di 1 ms, come fanno i programmi video (OBS
        // compreso); da Windows 10 2004 vale solo per questo processo.
        TimeBeginPeriod(1);
        try
        {
            while (!stop.IsCancellationRequested)
                _nuovaImmagine.WaitOne(Giro());
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    /// <summary>Manda in onda l'immagine di adesso, se c'è, e dice quanto aspettare prima del prossimo controllo.</summary>
    internal TimeSpan Giro()
    {
        long? audio = _istanteAudio();
        if (_prendi(audio) is ImmaginePronta immagine)
            Presenta(immagine, audio);

        // La prossima immagine andrà in onda quando l'audio raggiungerà il suo istante.
        if (audio is long adesso && _prossimoIstante() is long prossimo)
            return TimeSpan.FromMicroseconds(Math.Clamp(prossimo - adesso, 1000, (long)AttesaMassima.TotalMicroseconds));
        return AttesaMassima;
    }

    private void Presenta(ImmaginePronta immagine, long? audio)
    {
        if (audio is long istanteAudio)
        {
            lock (_bloccoScarti)
                _scartiAudioVideoMs.Add((istanteAudio - immagine.IstanteCatturaMicrosecondi) / 1000.0);
        }

        RotazioneVideo rotazione = _rotazione.DaIPhone(immagine.Rotazione);
        if (_bacheca is not null)
            ScriviInBacheca(immagine, rotazione);
        if (_finestraVisibile)
            PreparaPerFinestra(immagine, rotazione);

        _restituisci(immagine);
        Interlocked.Increment(ref _presentate);
    }

    private void ScriviInBacheca(ImmaginePronta immagine, RotazioneVideo rotazione)
    {
        try
        {
            ulong adesso = BachecaObs.OraNs();
            if (rotazione == RotazioneVideo.Nessuna)
            {
                _bacheca!.ScriviImmagine(immagine.Nv12, immagine.Larghezza, immagine.Altezza, immagine.Passo, adesso);
                Volatile.Write(ref _msRotazione, 0);
            }
            else
            {
                var cronometro = Stopwatch.StartNew();
                int dimensione = immagine.Larghezza * immagine.Altezza * 3 / 2;
                if (_girata.Length != dimensione)
                    _girata = new byte[dimensione];
                (int larghezza, int altezza) = RotazioneNv12.Ruota(immagine.Nv12, immagine.Larghezza, immagine.Altezza,
                    immagine.Passo, rotazione, _girata);
                Volatile.Write(ref _msRotazione, cronometro.Elapsed.TotalMilliseconds);
                _bacheca!.ScriviImmagine(_girata, larghezza, altezza, larghezza, adesso);
            }
            Interlocked.Increment(ref _scritteInBacheca);
        }
        catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException)
        {
            // Un'immagine che la bacheca non accetta non deve fermare la finestra.
            Interlocked.Increment(ref _erroriBacheca);
        }
    }

    private void PreparaPerFinestra(ImmaginePronta immagine, RotazioneVideo rotazione)
    {
        var cronometro = Stopwatch.StartNew();
        byte[] bgra = PrendiBgra(immagine.Larghezza * immagine.Altezza * 4);
        ConversioneColori.Nv12ABgra(immagine.Nv12, immagine.Larghezza, immagine.Altezza, immagine.Passo, bgra, immagine.Larghezza * 4);
        var pronta = new ImmagineFinestra(bgra, immagine.Larghezza, immagine.Altezza, rotazione,
            immagine.IstanteCatturaMicrosecondi, immagine.MsDecompressione, cronometro.Elapsed.TotalMilliseconds);

        // Se la finestra non ha ancora disegnato la precedente, quella non serve più.
        if (Interlocked.Exchange(ref _perFinestra, pronta) is ImmagineFinestra saltata)
            _bgraLiberi.Add(saltata.Bgra);
    }

    private byte[] PrendiBgra(int dimensione)
    {
        // I buffer di dimensione diversa (risoluzione cambiata) si lasciano al garbage collector.
        while (_bgraLiberi.TryTake(out byte[]? buffer))
        {
            if (buffer.Length == dimensione)
                return buffer;
        }
        return new byte[dimensione];
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint millisecondi);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint millisecondi);
}
