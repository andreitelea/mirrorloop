namespace MirrorLoop.Ricevitore;

/// <summary>
/// La "riserva" tra la rete e la scheda audio. I pacchetti arrivano a scatti (la rete ha
/// piccoli ritardi), la scheda audio invece chiede campioni a ritmo perfettamente regolare:
/// tenere da parte qualche decina di millisecondi evita i buchi nel suono.
///
/// Regole:
/// - all'inizio, e dopo ogni buco, si suona silenzio finché la riserva non raggiunge l'obiettivo;
/// - se la riserva supera il massimo (gli orologi di iPhone e PC non vanno esattamente alla
///   stessa velocità, o la rete ha consegnato tutto in una volta), si buttano i campioni più
///   vecchi fino a tornare all'obiettivo: meglio un piccolo salto che un ritardo che cresce.
///
/// Thread-safe: <see cref="Aggiungi"/> arriva dal thread di rete, <see cref="Leggi"/> da quello audio.
/// </summary>
public sealed class RiservaAudio
{
    private readonly object _blocco = new();
    private readonly byte[] _anello;
    private readonly int _byteAlSecondo;
    private readonly int _byteObiettivo;
    private readonly int _byteMassimi;
    private readonly int _byteBlocco;
    private int _inizio;
    private int _disponibili;
    private bool _inRiempimento = true;

    /// <param name="byteAlSecondo">Es. 48 000 Hz × 2 canali × 2 byte = 192 000.</param>
    /// <param name="byteBlocco">Byte di un istante (tutti i canali): non si spezza mai un campione.</param>
    public RiservaAudio(int byteAlSecondo, int byteBlocco, TimeSpan obiettivo, TimeSpan massimo)
    {
        if (massimo <= obiettivo)
            throw new ArgumentException("Il massimo deve superare l'obiettivo.", nameof(massimo));
        _byteAlSecondo = byteAlSecondo;
        _byteBlocco = byteBlocco;
        _byteObiettivo = Allinea((int)(byteAlSecondo * obiettivo.TotalSeconds));
        _byteMassimi = Allinea((int)(byteAlSecondo * massimo.TotalSeconds));
        _anello = new byte[Allinea(byteAlSecondo * 2)];
    }

    /// <summary>Quante volte la riserva si è svuotata mentre si suonava.</summary>
    public long Buchi { get; private set; }

    /// <summary>Quante volte si sono buttati campioni perché la riserva era troppo piena.</summary>
    public long Tagli { get; private set; }

    public TimeSpan Riserva
    {
        get
        {
            lock (_blocco)
                return TimeSpan.FromSeconds((double)_disponibili / _byteAlSecondo);
        }
    }

    public void Aggiungi(ReadOnlySpan<byte> dati)
    {
        lock (_blocco)
        {
            // Un pacchetto più grande dell'intero anello: conta solo la sua parte finale.
            if (dati.Length > _anello.Length)
                dati = dati[^_anello.Length..];

            int fine = (_inizio + _disponibili) % _anello.Length;
            int primoPezzo = Math.Min(dati.Length, _anello.Length - fine);
            dati[..primoPezzo].CopyTo(_anello.AsSpan(fine));
            dati[primoPezzo..].CopyTo(_anello);
            _disponibili += dati.Length;

            if (_disponibili > _anello.Length)
                Scarta(_disponibili - _anello.Length);
            if (_disponibili > _byteMassimi)
            {
                Scarta(_disponibili - _byteObiettivo);
                Tagli++;
            }
        }
    }

    /// <summary>
    /// Riempie sempre tutto <paramref name="destinazione"/>: con i campioni in riserva se ci sono,
    /// altrimenti con silenzio. Restituisce i byte scritti (cioè la lunghezza della destinazione).
    /// </summary>
    public int Leggi(Span<byte> destinazione)
    {
        lock (_blocco)
        {
            if (_inRiempimento)
            {
                if (_disponibili < _byteObiettivo)
                {
                    destinazione.Clear();
                    return destinazione.Length;
                }
                _inRiempimento = false;
            }

            int daCopiare = Math.Min(destinazione.Length, _disponibili);
            int primoPezzo = Math.Min(daCopiare, _anello.Length - _inizio);
            _anello.AsSpan(_inizio, primoPezzo).CopyTo(destinazione);
            _anello.AsSpan(0, daCopiare - primoPezzo).CopyTo(destinazione[primoPezzo..]);
            _inizio = (_inizio + daCopiare) % _anello.Length;
            _disponibili -= daCopiare;

            if (daCopiare < destinazione.Length)
            {
                destinazione[daCopiare..].Clear();
                _inRiempimento = true;
                Buchi++;
            }
            return destinazione.Length;
        }
    }

    /// <summary>Butta tutto e ricomincia dal riempimento (per esempio a una nuova connessione).</summary>
    public void Svuota()
    {
        lock (_blocco)
        {
            _inizio = 0;
            _disponibili = 0;
            _inRiempimento = true;
        }
    }

    private void Scarta(int byteDaScartare)
    {
        byteDaScartare = Math.Min(Allinea(byteDaScartare + _byteBlocco - 1), _disponibili);
        _inizio = (_inizio + byteDaScartare) % _anello.Length;
        _disponibili -= byteDaScartare;
    }

    private int Allinea(int byteTotali) => byteTotali / _byteBlocco * _byteBlocco;
}
