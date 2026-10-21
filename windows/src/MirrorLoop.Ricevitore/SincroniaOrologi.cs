using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Stima di quanto l'orologio dell'iPhone è spostato rispetto a quello del PC, con il metodo NTP:
///
///   T1 = PC manda il ping        T2 = iPhone lo riceve
///   T3 = iPhone manda il pong    T4 = PC lo riceve
///
///   scarto          = ((T2 − T1) + (T3 − T4)) / 2     (ora iPhone − ora PC)
///   andata/ritorno  = (T4 − T1) − (T3 − T2)           (tempo passato in rete)
///
/// Il calcolo è esatto se andata e ritorno durano uguale. Sulla nostra connessione il ritorno può
/// restare in coda dietro ai frame video: per questo, tra le ultime misure, si tiene quella con
/// l'andata/ritorno più breve, la meno disturbata.
///
/// Thread-safe: <see cref="Registra"/> dal thread di rete, il resto dalla finestra.
/// </summary>
public sealed class SincroniaOrologi
{
    /// <summary>Quante misure recenti si considerano (una al secondo: circa mezzo minuto).</summary>
    public const int MisureRecenti = 30;

    private readonly object _blocco = new();
    private readonly Queue<Misura> _misure = new();

    public readonly record struct Misura(long ScartoMicrosecondi, long AndataRitornoMicrosecondi);

    /// <summary>La misura migliore tra le recenti, o null se non ce n'è ancora nessuna.</summary>
    public Misura? Migliore
    {
        get
        {
            lock (_blocco)
                return _misure.Count == 0 ? null : _misure.MinBy(m => m.AndataRitornoMicrosecondi);
        }
    }

    /// <param name="t4">Ora del PC all'arrivo del pong.</param>
    public void Registra(Pong pong, long t4)
    {
        long andataRitorno = (t4 - pong.T1) - (pong.T3 - pong.T2);
        // Un valore negativo vorrebbe dire dati incoerenti (per esempio un pong di un'altra connessione).
        if (andataRitorno < 0)
            return;
        long scarto = ((pong.T2 - pong.T1) + (pong.T3 - t4)) / 2;
        lock (_blocco)
        {
            _misure.Enqueue(new Misura(scarto, andataRitorno));
            if (_misure.Count > MisureRecenti)
                _misure.Dequeue();
        }
    }

    /// <summary>
    /// Converte un istante dell'orologio dell'iPhone nell'ora del PC, o null se gli orologi non
    /// sono ancora sincronizzati.
    /// </summary>
    public long? InOraDelPc(long istanteIphone) =>
        Migliore is Misura m ? istanteIphone - m.ScartoMicrosecondi : null;

    /// <summary>A una nuova connessione le misure vecchie non valgono più (l'iPhone può essere un altro).</summary>
    public void Ricomincia()
    {
        lock (_blocco)
            _misure.Clear();
    }
}
