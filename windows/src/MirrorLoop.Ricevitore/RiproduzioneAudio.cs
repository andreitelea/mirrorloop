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
    // ~60 ms di riserva bastano per i piccoli ritardi della rete di casa; oltre 200 ms si torna
    // indietro (vedi RiservaAudio). La latenza di WASAPI si aggiunge a questi valori.
    public static readonly TimeSpan RiservaObiettivo = TimeSpan.FromMilliseconds(60);
    public static readonly TimeSpan RiservaMassima = TimeSpan.FromMilliseconds(200);
    private const int LatenzaWasapiMs = 30;

    private readonly object _blocco = new();
    private WasapiOut? _uscita;
    private RiservaAudio? _riserva;
    private (int Canali, int Frequenza) _formato;

    /// <summary>Nome del dispositivo audio in uso, o il motivo per cui non suona.</summary>
    public string Stato { get; private set; } = "in attesa di audio";

    public TimeSpan Riserva => _riserva?.Riserva ?? TimeSpan.Zero;
    public long Buchi => _riserva?.Buchi ?? 0;
    public long Tagli => _riserva?.Tagli ?? 0;

    /// <summary>Chiamato dal thread di rete per ogni pacchetto ricevuto.</summary>
    public void Aggiungi(PacchettoAudio audio)
    {
        if (audio.Codifica != CodificaAudio.Pcm16)
            return;

        lock (_blocco)
        {
            if (_uscita is null || _formato != (audio.Canali, audio.Frequenza))
                Apri(audio.Canali, audio.Frequenza);
            _riserva?.Aggiungi(audio.Dati.Span);
        }
    }

    /// <summary>A una nuova connessione si riparte da zero, senza avanzi della precedente.</summary>
    public void Ricomincia() => _riserva?.Svuota();

    private void Apri(int canali, int frequenza)
    {
        Chiudi();
        _formato = (canali, frequenza);
        try
        {
            int byteBlocco = canali * 2;
            var riserva = new RiservaAudio(frequenza * byteBlocco, byteBlocco, RiservaObiettivo, RiservaMassima);
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
