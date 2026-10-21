using MirrorLoop.Protocollo;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Suona l'audio ricevuto sull'uscita predefinita di Windows (WASAPI, modalità condivisa).
/// L'uscita si apre al primo pacchetto e si riapre se cambia il formato.
/// </summary>
public sealed class RiproduzioneAudio : IDisposable
{
    // Riserva da 60 ms con la rete tranquilla, fino a 100 dopo le pause (vedi RiservaAudio). Il
    // tetto è basso di proposito: lo schermo deve restare il più possibile in tempo reale rispetto
    // a una webcam, e con un ritardo quasi costante. Simulazione sui registri del 21/10/2026: con
    // tetto 100 da 2 a 7 buchi ogni 5 minuti contro 13–16 con 60 fisso; oltre 100 quasi nessun
    // guadagno. Fuori da ±20 ms si corregge la velocità, oltre 140 si torna subito all'obiettivo.
    // La latenza di WASAPI si aggiunge.
    public static readonly TimeSpan RiservaMinima = TimeSpan.FromMilliseconds(60);
    public static readonly TimeSpan RiservaTetto = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan RiservaTolleranza = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan RiservaMassima = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan Dissolvenza = TimeSpan.FromMilliseconds(5);
    private const int LatenzaWasapiMs = 30;

    // Quanto passa tra la consegna dei campioni a WASAPI e il suono nelle cuffie. È una stima
    // (il buffer di WASAPI): con cuffie Bluetooth sarebbe molto di più e andrà resa regolabile.
    private static readonly TimeSpan LatenzaUscita = TimeSpan.FromMilliseconds(LatenzaWasapiMs);

    private readonly object _blocco = new();
    private WasapiOut? _uscita;
    private RiservaAudio? _riserva;
    private (int Canali, int Frequenza) _formato;

    /// <summary>Nome del dispositivo audio in uso, o il motivo per cui non suona.</summary>
    public string Stato { get; private set; } = "in attesa di audio";

    public TimeSpan Riserva => _riserva?.Riserva ?? TimeSpan.Zero;
    public TimeSpan Obiettivo => _riserva?.Obiettivo ?? RiservaMinima;
    public long Buchi => _riserva?.Buchi ?? 0;
    public long Tagli => _riserva?.Tagli ?? 0;
    public double Velocita => _riserva?.Velocita ?? 1;

    /// <summary>Buchi e tagli della riserva (vedi <see cref="RiservaAudio.EventoAvvenuto"/>).</summary>
    public event Action<RiservaAudio.Evento>? EventoRiserva;

    /// <summary>Chiamato dal thread di rete per ogni pacchetto ricevuto.</summary>
    public void Aggiungi(PacchettoAudio audio)
    {
        if (audio.Codifica != CodificaAudio.Pcm16)
            return;

        lock (_blocco)
        {
            if (_uscita is null || _formato != (audio.Canali, audio.Frequenza))
                Apri(audio.Canali, audio.Frequenza);
            _riserva?.Aggiungi(audio.Dati.Span, audio.IstanteCatturaMicrosecondi);
        }
    }

    /// <summary>L'istante di cattura che si sente adesso, o null se l'audio non sta suonando.</summary>
    public long? IstanteInAscolto() => _riserva?.IstanteInAscolto(LatenzaUscita);

    /// <summary>A una nuova connessione si riparte da zero, senza avanzi della precedente.</summary>
    public void Ricomincia() => _riserva?.Svuota();

    private void Apri(int canali, int frequenza)
    {
        Chiudi();
        _formato = (canali, frequenza);
        try
        {
            int byteBlocco = canali * 2;
            var riserva = new RiservaAudio(frequenza * byteBlocco, canali, RiservaMinima, RiservaTetto, RiservaTolleranza,
                RiservaMassima, Dissolvenza);
            riserva.EventoAvvenuto += e => EventoRiserva?.Invoke(e);
            var uscita = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, LatenzaWasapiMs);
            uscita.Init(new SorgenteRiserva(riserva, new WaveFormat(frequenza, 16, canali)));
            uscita.Play();
            _riserva = riserva;
            _uscita = uscita;
            using var dispositivi = new MMDeviceEnumerator();
            Stato = dispositivi.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch (Exception ex)
        {
            // Senza scheda audio (o con il dispositivo occupato) il video deve continuare lo stesso.
            Chiudi();
            Stato = $"audio non disponibile: {ex.Message}";
        }
    }

    private void Chiudi()
    {
        _uscita?.Stop();
        _uscita?.Dispose();
        _uscita = null;
        _riserva = null;
    }

    public void Dispose()
    {
        lock (_blocco)
            Chiudi();
    }

    /// <summary>Ponte tra NAudio, che chiede i campioni, e la riserva che li fornisce.</summary>
    private sealed class SorgenteRiserva(RiservaAudio riserva, WaveFormat formato) : IWaveProvider
    {
        public WaveFormat WaveFormat => formato;

        public int Read(byte[] buffer, int offset, int count) => riserva.Leggi(buffer.AsSpan(offset, count));
    }
}
