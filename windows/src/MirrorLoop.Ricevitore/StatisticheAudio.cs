using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Contatori dell'audio ricevuto, aggiornati dal thread di rete e letti dalla finestra.
/// Come <see cref="StatisticheRicezione"/>, ogni lettura riparte da zero.
/// </summary>
public sealed class StatisticheAudio
{
    private readonly object _blocco = new();
    private long _pacchetti;
    private long _byte;
    private double _msAudio;
    private long _ignorati;
    private string _formato = "—";

    public void PacchettoRicevuto(PacchettoAudio audio)
    {
        lock (_blocco)
        {
            _pacchetti++;
            _byte += audio.Dati.Length;
            if (audio.Codifica == CodificaAudio.Pcm16)
            {
                _msAudio += audio.Durata.TotalMilliseconds;
                _formato = $"PCM 16 bit, {audio.Canali} canali, {audio.Frequenza} Hz";
            }
            else
            {
                _ignorati++;
            }
        }
    }

    public Istantanea Leggi()
    {
        lock (_blocco)
        {
            var risultato = new Istantanea(_pacchetti, _byte, _msAudio, _ignorati, _formato);
            _pacchetti = _byte = _ignorati = 0;
            _msAudio = 0;
            return risultato;
        }
    }

    /// <param name="MsAudio">Millisecondi di audio contenuti nei pacchetti: ~1000 al secondo se il flusso è completo.</param>
    /// <param name="Ignorati">Pacchetti con una codifica che questo ricevitore non conosce.</param>
    public readonly record struct Istantanea(long Pacchetti, long Byte, double MsAudio, long Ignorati, string Formato);
}
