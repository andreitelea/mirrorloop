namespace MirrorLoop.Ricevitore;

/// <summary>
/// Le "informazioni sulla trasmissione" (tasto S): poche righe in parole semplici per chi usa il
/// programma. I numeri completi stanno nelle statistiche (Ctrl+S), per la diagnosi.
/// </summary>
public static class InformazioniTrasmissione
{
    /// <summary>
    /// Il nome della qualità scelta sull'iPhone, ricavato dalle misure delle immagini: il lato corto è
    /// 720 (HD), 1088 (Full HD) o quello dello schermo (Massima: 1170 sull'iPhone 14, 1320 sul 17 Pro Max).
    /// </summary>
    public static string Qualita(int larghezza, int altezza)
    {
        int latoCorto = Math.Min(larghezza, altezza);
        return latoCorto <= 800 ? Testi.InfoQualitaHD
            : latoCorto <= 1100 ? Testi.InfoQualitaFullHD
            : Testi.InfoQualitaMassima;
    }

    /// <summary>Il ritardo dallo schermo dell'iPhone alla finestra, in decimi di secondo: basta a farsi un'idea.</summary>
    public static string Ritardo(double? millisecondi) =>
        millisecondi is double ms ? string.Format(Testi.InfoRitardoValore, ms / 1000) : "—";

    public static string Interruzioni(long quante) => quante switch
    {
        0 => Testi.InfoInterruzioniNessuna,
        1 => Testi.InfoInterruzioniUna,
        _ => string.Format(Testi.InfoInterruzioniMolte, quante),
    };
}

/// <summary>
/// Di quanto è cresciuto un contatore (per esempio i buchi dell'audio) nell'ultima finestra di tempo,
/// dai valori letti a intervalli regolari. Se il contatore riparte da zero (nuova connessione) si
/// ricomincia da lì.
/// </summary>
public sealed class ContoUltimoMinuto(TimeSpan finestra)
{
    private readonly Queue<(TimeSpan Istante, long Totale)> _campioni = new();
    private long _ultimo;

    public long Aggiungi(TimeSpan istante, long totale)
    {
        if (totale < _ultimo)
        {
            _campioni.Clear();
            _campioni.Enqueue((istante, 0));
        }
        _ultimo = totale;
        _campioni.Enqueue((istante, totale));
        while (_campioni.Count > 1 && istante - _campioni.Peek().Istante > finestra)
            _campioni.Dequeue();
        return totale - _campioni.Peek().Totale;
    }
}
