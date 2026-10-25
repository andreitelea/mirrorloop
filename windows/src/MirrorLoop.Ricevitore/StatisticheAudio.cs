using System.Diagnostics;
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
    private long _ultimoArrivo;
    private double _pausaMassimaMs;
    private double _pausaMassimaTotaleMs;

    public void PacchettoRicevuto(PacchettoAudio audio)
    {
        long adesso = Stopwatch.GetTimestamp();
        lock (_blocco)
        {
            // La pausa più lunga tra due arrivi dice quanta riserva serve per non avere buchi.
            if (_ultimoArrivo != 0)
            {
                double pausaMs = Stopwatch.GetElapsedTime(_ultimoArrivo, adesso).TotalMilliseconds;
                _pausaMassimaMs = Math.Max(_pausaMassimaMs, pausaMs);
                _pausaMassimaTotaleMs = Math.Max(_pausaMassimaTotaleMs, pausaMs);
            }
            _ultimoArrivo = adesso;
            _pacchetti++;
            _byte += audio.Dati.Length;
            if (audio.Codifica == CodificaAudio.Pcm16)
            {
                _msAudio += audio.Durata.TotalMilliseconds;
                _formato = string.Format(Testi.FormatoAudio, audio.Canali, audio.Frequenza);
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
            var risultato = new Istantanea(_pacchetti, _byte, _msAudio, _ignorati, _formato, _pausaMassimaMs, _pausaMassimaTotaleMs);
            _pacchetti = _byte = _ignorati = 0;
            _msAudio = _pausaMassimaMs = 0;
            return risultato;
        }
    }

    /// <summary>A una nuova connessione la pausa dall'ultimo pacchetto della precedente non conta.</summary>
    public void Ricomincia()
    {
        lock (_blocco)
        {
            _ultimoArrivo = 0;
            _pausaMassimaTotaleMs = 0;
        }
    }

    /// <param name="MsAudio">Millisecondi di audio contenuti nei pacchetti: ~1000 al secondo se il flusso è completo.</param>
    /// <param name="Ignorati">Pacchetti con una codifica che questo ricevitore non conosce.</param>
    /// <param name="PausaMassimaMs">Pausa più lunga tra due pacchetti dall'ultima lettura.</param>
    /// <param name="PausaMassimaTotaleMs">Pausa più lunga dall'inizio della connessione.</param>
    public readonly record struct Istantanea(
        long Pacchetti, long Byte, double MsAudio, long Ignorati, string Formato, double PausaMassimaMs, double PausaMassimaTotaleMs);
}
