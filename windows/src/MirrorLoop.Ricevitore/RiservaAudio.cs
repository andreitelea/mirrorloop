using System.Buffers.Binary;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// La "riserva" tra la rete e la scheda audio. I pacchetti arrivano a scatti (la rete ha
/// piccoli ritardi, il Wi-Fi del telefono spedisce a gruppi), la scheda audio invece chiede
/// campioni a ritmo perfettamente regolare: tenere da parte qualche decina di millisecondi
/// evita i buchi nel suono.
///
/// Regole:
/// - all'inizio, e dopo ogni buco, si suona silenzio finché la riserva non raggiunge l'obiettivo;
/// - se la riserva si allontana dall'obiettivo (una raffica l'ha riempita troppo, o una pausa
///   l'ha quasi svuotata), si suona un poco più veloce o più lenti (±0,5%, non percepibile)
///   finché non torna all'obiettivo: è la tecnica delle app di videochiamata;
/// - solo oltre il massimo si buttano di colpo i campioni più vecchi (emergenza).
///
/// Il cambio di velocità si fa per interpolazione lineare tra campioni vicini: con una
/// differenza di 0,5% l'errore introdotto è trascurabile.
///
/// Formato: PCM 16 bit little-endian, canali intercalati.
/// Thread-safe: <see cref="Aggiungi"/> arriva dal thread di rete, <see cref="Leggi"/> da quello audio.
/// </summary>
public sealed class RiservaAudio
{
    public const double VariazioneVelocita = 0.005;

    private readonly object _blocco = new();
    private readonly byte[] _anello;
    private readonly int _byteAlSecondo;
    private readonly int _canali;
    private readonly int _byteBlocco;
    private readonly int _byteObiettivo;
    private readonly int _byteMassimi;
    private readonly int _byteTolleranza;
    private int _inizio;
    private int _disponibili;
    private bool _inRiempimento = true;
    private double _fase;

    /// <param name="byteAlSecondo">Es. 48 000 Hz × 2 canali × 2 byte = 192 000.</param>
    /// <param name="canali">Numero di canali (campioni da 16 bit per istante).</param>
    /// <param name="obiettivo">Riserva a cui tendere.</param>
    /// <param name="tolleranza">Oltre obiettivo ± tolleranza si cambia velocità.</param>
    /// <param name="massimo">Oltre questo valore si taglia di colpo.</param>
    public RiservaAudio(int byteAlSecondo, int canali, TimeSpan obiettivo, TimeSpan tolleranza, TimeSpan massimo)
    {
        if (massimo <= obiettivo + tolleranza)
            throw new ArgumentException("Il massimo deve superare obiettivo + tolleranza.", nameof(massimo));
        _byteAlSecondo = byteAlSecondo;
        _canali = canali;
        _byteBlocco = canali * 2;
        _byteObiettivo = Allinea((int)(byteAlSecondo * obiettivo.TotalSeconds));
        _byteTolleranza = Allinea((int)(byteAlSecondo * tolleranza.TotalSeconds));
        _byteMassimi = Allinea((int)(byteAlSecondo * massimo.TotalSeconds));
        _anello = new byte[Allinea(byteAlSecondo * 2)];
    }

    /// <summary>Quante volte la riserva si è svuotata mentre si suonava.</summary>
    public long Buchi { get; private set; }

    /// <summary>Quante volte si sono buttati campioni perché la riserva superava il massimo.</summary>
    public long Tagli { get; private set; }

    /// <summary>1 = velocità normale; 1,005 = un po' più veloce; 0,995 = un po' più lenta.</summary>
    public double Velocita { get; private set; } = 1;

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
                Velocita = 1;
                _fase = 0;
            }

            AggiornaVelocita();

            int istantiDaScrivere = destinazione.Length / _byteBlocco;
            int istantiDisponibili = _disponibili / _byteBlocco;
            int scritti = 0;
            double posizione = _fase;
            while (scritti < istantiDaScrivere)
            {
                int indice = (int)posizione;
                double frazione = posizione - indice;
                // Per interpolare serve anche l'istante successivo, se la frazione non è zero.
                int necessari = frazione > 0 ? indice + 2 : indice + 1;
                if (necessari > istantiDisponibili)
                    break;
                ScriviIstante(destinazione.Slice(scritti * _byteBlocco, _byteBlocco), indice, frazione);
                scritti++;
                posizione += Velocita;
            }

            int consumati = Math.Min((int)posizione, istantiDisponibili);
            _fase = posizione - (int)posizione;
            _inizio = (_inizio + consumati * _byteBlocco) % _anello.Length;
            _disponibili -= consumati * _byteBlocco;

            if (scritti < istantiDaScrivere)
            {
                destinazione[(scritti * _byteBlocco)..].Clear();
                _inRiempimento = true;
                Buchi++;
            }
            else
            {
                destinazione[(istantiDaScrivere * _byteBlocco)..].Clear();
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
            _fase = 0;
            Velocita = 1;
        }
    }

    /// <summary>
    /// Fuori dalla tolleranza si cambia velocità; si torna a velocità normale solo dopo aver
    /// riattraversato l'obiettivo, così non si salta avanti e indietro al confine.
    /// </summary>
    private void AggiornaVelocita()
    {
        if (_disponibili > _byteObiettivo + _byteTolleranza)
            Velocita = 1 + VariazioneVelocita;
        else if (_disponibili < _byteObiettivo - _byteTolleranza)
            Velocita = 1 - VariazioneVelocita;
        else if ((Velocita > 1 && _disponibili <= _byteObiettivo) || (Velocita < 1 && _disponibili >= _byteObiettivo))
            Velocita = 1;
    }

    private void ScriviIstante(Span<byte> uscita, int indice, double frazione)
    {
        int primo = (_inizio + indice * _byteBlocco) % _anello.Length;
        int secondo = (primo + _byteBlocco) % _anello.Length;
        for (int canale = 0; canale < _canali; canale++)
        {
            short a = BinaryPrimitives.ReadInt16LittleEndian(_anello.AsSpan(primo + canale * 2));
            short valore = a;
            if (frazione > 0)
            {
                short b = BinaryPrimitives.ReadInt16LittleEndian(_anello.AsSpan(secondo + canale * 2));
                valore = (short)Math.Round(a + (b - a) * frazione);
            }
            BinaryPrimitives.WriteInt16LittleEndian(uscita[(canale * 2)..], valore);
        }
    }

    private void Scarta(int byteDaScartare)
    {
        byteDaScartare = Math.Min(Allinea(byteDaScartare + _byteBlocco - 1), _disponibili);
        _inizio = (_inizio + byteDaScartare) % _anello.Length;
        _disponibili -= byteDaScartare;
        _fase = 0;
    }

    private int Allinea(int byteTotali) => byteTotali / _byteBlocco * _byteBlocco;
}
