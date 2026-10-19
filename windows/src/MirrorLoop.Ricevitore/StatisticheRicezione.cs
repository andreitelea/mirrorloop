namespace MirrorLoop.Ricevitore;

/// <summary>
/// Contatori aggiornati dal thread di rete e letti dalla finestra. Ogni lettura restituisce
/// i valori dall'ultima lettura in poi, così la finestra mostra "l'ultimo mezzo secondo".
/// </summary>
public sealed class StatisticheRicezione
{
    private readonly object _blocco = new();
    private long _frame;
    private long _byte;
    private long _keyframe;
    private readonly List<double> _latenzeMs = [];

    public void FrameRicevuto(bool keyframe, int dimensione, double latenzaMs)
    {
        lock (_blocco)
        {
            _frame++;
            _byte += dimensione;
            if (keyframe)
                _keyframe++;
            _latenzeMs.Add(latenzaMs);
        }
    }

    public Istantanea Leggi()
    {
        lock (_blocco)
        {
            var ordinate = _latenzeMs.Order().ToArray();
            var risultato = new Istantanea(
                _frame, _byte, _keyframe,
                ordinate.Length > 0 ? ordinate[ordinate.Length / 2] : null,
                ordinate.Length > 0 ? ordinate[^1] : null);
            _frame = _byte = _keyframe = 0;
            _latenzeMs.Clear();
            return risultato;
        }
    }

    public readonly record struct Istantanea(long Frame, long Byte, long Keyframe, double? LatenzaMedianaMs, double? LatenzaMassimaMs);
}
