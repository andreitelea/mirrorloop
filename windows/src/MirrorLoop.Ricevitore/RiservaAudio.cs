using System.Buffers.Binary;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// La "riserva" tra la rete e la scheda audio. I pacchetti arrivano a scatti (la rete ha
/// piccoli ritardi, il Wi-Fi del telefono spedisce a gruppi), la scheda audio invece chiede
/// campioni a ritmo perfettamente regolare: tenere da parte qualche decina di millisecondi
/// evita i buchi nel suono. Ogni millisecondo di riserva è però un millisecondo di ritardo, anche
/// per il video (che aspetta l'audio): la riserva è il compromesso tra buchi e tempo reale.
///
/// Regole:
/// - l'obiettivo si adatta alla rete: copre la pausa più lunga tra due arrivi dell'ultimo minuto
///   (più la tolleranza), senza mai scendere sotto il minimo né salire oltre il tetto. Dopo un
///   minuto senza pause torna al minimo. È l'idea della riserva delle app di videochiamata (WebRTC);
/// - all'inizio, e dopo ogni buco, si suona silenzio finché la riserva non raggiunge l'obiettivo;
/// - se la riserva si allontana dall'obiettivo, si suona un poco più veloce o più lenti (±0,5%,
///   non percepibile) finché non torna all'obiettivo;
/// - oltre il massimo (di solito dopo una pausa lunga, quando i pacchetti trattenuti arrivano tutti
///   insieme) si buttano subito i campioni più vecchi e si torna all'obiettivo: meglio perdere un
///   pezzo già in ritardo che restare indietro per decine di secondi;
/// - ogni discontinuità è ammorbidita da una breve dissolvenza (inizio e fine di un buco, salto
///   del taglio): un salto netto della forma d'onda si sente come un "clic".
///
/// Il cambio di velocità si fa per interpolazione lineare tra campioni vicini: con una
/// differenza di 0,5% l'errore introdotto è trascurabile.
///
/// Tiene anche il conto di "che istante di cattura sto suonando", che serve a sincronizzare
/// il video con l'audio (vedi <see cref="IstanteInAscolto"/>).
///
/// Formato: PCM 16 bit little-endian, canali intercalati.
/// Thread-safe: <see cref="Aggiungi"/> arriva dal thread di rete, <see cref="Leggi"/> da quello audio.
/// </summary>
public sealed class RiservaAudio
{
    public const double VariazioneVelocita = 0.005;

    /// <summary>Per quanto tempo una pausa tiene alto l'obiettivo.</summary>
    public static readonly TimeSpan FinestraPause = TimeSpan.FromSeconds(60);

    private readonly object _blocco = new();
    private readonly TimeProvider _tempo;
    private readonly byte[] _anello;
    private readonly int _byteAlSecondo;
    private readonly int _canali;
    private readonly int _byteBlocco;
    private readonly int _byteObiettivoMinimo;
    private readonly int _byteTetto;
    private readonly int _byteMassimi;
    private readonly int _byteTolleranza;
    private int _byteObiettivo;
    private int _inizio;
    private int _disponibili;
    private bool _inRiempimento = true;
    private double _fase;

    // Le pause notevoli (quelle che chiedono più del minimo) con il momento in cui sono finite e
    // la riserva che avrebbero richiesto.
    private readonly Queue<(long Momento, int Byte)> _pause = new();
    private long? _ultimoArrivo;

    // Dissolvenze, misurate in istanti (un istante = un campione per ogni canale).
    private readonly int _istantiDissolvenza;
    private int _entrataFatti;
    private readonly short[] _vecchi;
    private int _incrocioFatti;
    private int _incrocioTotali;

    // Per sapere quale istante di cattura si sta suonando: posizioni "assolute" (byte scritti e
    // letti dall'inizio) e, per ogni pacchetto, l'istante di cattura del suo primo campione.
    private readonly Queue<(long Posizione, long IstanteMicrosecondi)> _ancore = new();
    private long _scritti;
    private long _letti;
    private long? _istanteUltimaLettura;
    private long _momentoUltimaLettura;

    /// <param name="byteAlSecondo">Es. 48 000 Hz × 2 canali × 2 byte = 192 000.</param>
    /// <param name="canali">Numero di canali (campioni da 16 bit per istante).</param>
    /// <param name="obiettivoMinimo">Riserva a cui tendere quando la rete è tranquilla.</param>
    /// <param name="tetto">Oltre questo l'obiettivo non sale mai, qualunque pausa ci sia stata.</param>
    /// <param name="tolleranza">Oltre obiettivo ± tolleranza si cambia velocità; è anche il margine aggiunto alle pause.</param>
    /// <param name="massimo">Oltre questo valore si torna di colpo all'obiettivo.</param>
    /// <param name="dissolvenza">Durata delle dissolvenze; zero per nessuna.</param>
    /// <param name="tempo">Orologio (sostituibile nei test).</param>
    public RiservaAudio(int byteAlSecondo, int canali, TimeSpan obiettivoMinimo, TimeSpan tetto, TimeSpan tolleranza,
        TimeSpan massimo, TimeSpan dissolvenza, TimeProvider? tempo = null)
    {
        if (tetto < obiettivoMinimo)
            throw new ArgumentException("Il tetto non può stare sotto l'obiettivo minimo.", nameof(tetto));
        if (massimo <= tetto + tolleranza)
            throw new ArgumentException("Il massimo deve superare tetto + tolleranza.", nameof(massimo));
        _tempo = tempo ?? TimeProvider.System;
        _byteAlSecondo = byteAlSecondo;
        _canali = canali;
        _byteBlocco = canali * 2;
        _byteObiettivoMinimo = Byte(obiettivoMinimo);
        _byteTetto = Byte(tetto);
        _byteTolleranza = Byte(tolleranza);
        _byteMassimi = Byte(massimo);
        _byteObiettivo = _byteObiettivoMinimo;
        _istantiDissolvenza = Byte(dissolvenza) / _byteBlocco;
        _vecchi = new short[_istantiDissolvenza * canali];
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
                return Durata(_disponibili);
        }
    }

    /// <summary>La riserva a cui si tende adesso: sale dopo le pause, fino al tetto.</summary>
    public TimeSpan Obiettivo
    {
        get
        {
            lock (_blocco)
                return Durata(_byteObiettivo);
        }
    }

    /// <param name="istanteCatturaMicrosecondi">Istante di cattura del primo campione di <paramref name="dati"/>.</param>
    public void Aggiungi(ReadOnlySpan<byte> dati, long istanteCatturaMicrosecondi = 0)
    {
        lock (_blocco)
        {
            AggiornaObiettivo();

            // Un pacchetto più grande dell'intero anello: conta solo la sua parte finale.
            if (dati.Length > _anello.Length)
            {
                istanteCatturaMicrosecondi += DurataMicrosecondi(dati.Length - _anello.Length);
                dati = dati[^_anello.Length..];
            }
            _ancore.Enqueue((_scritti, istanteCatturaMicrosecondi));
            _scritti += dati.Length;

            int fine = (_inizio + _disponibili) % _anello.Length;
            int primoPezzo = Math.Min(dati.Length, _anello.Length - fine);
            dati[..primoPezzo].CopyTo(_anello.AsSpan(fine));
            dati[primoPezzo..].CopyTo(_anello);
            _disponibili += dati.Length;

            if (_disponibili > _anello.Length)
                Scarta(_disponibili - _anello.Length);
            if (_disponibili > _byteMassimi)
            {
                // Se si sta suonando, i campioni che stavano per uscire sfumano in quelli nuovi.
                if (!_inRiempimento)
                    PreparaIncrocio();
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
                    _istanteUltimaLettura = null;
                    return destinazione.Length;
                }
                _inRiempimento = false;
                Velocita = 1;
                _fase = 0;
                _entrataFatti = 0;
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
            Avanza(consumati * _byteBlocco);
            _istanteUltimaLettura = scritti > 0 ? IstanteLetto() : null;
            _momentoUltimaLettura = _tempo.GetTimestamp();

            if (scritti < istantiDaScrivere)
            {
                SfumaInUscita(destinazione, scritti);
                destinazione[(scritti * _byteBlocco)..].Clear();
                _inRiempimento = true;
                _incrocioTotali = 0;
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
            _ancore.Clear();
            _scritti = _letti = 0;
            _istanteUltimaLettura = null;
            // Le pause della connessione precedente non dicono nulla di quella nuova.
            _pause.Clear();
            _ultimoArrivo = null;
            _byteObiettivo = _byteObiettivoMinimo;
            _incrocioTotali = 0;
        }
    }

    /// <summary>
    /// L'istante di cattura che si sente <em>adesso</em> nelle cuffie, o null se non si sta suonando
    /// audio vero (riempimento, buco). Parte dall'ultimo campione passato alla scheda audio, aggiunge
    /// il tempo trascorso da allora e toglie la latenza dell'uscita (i campioni consegnati alla
    /// scheda si sentono solo dopo che è passato il suo buffer).
    /// </summary>
    public long? IstanteInAscolto(TimeSpan latenzaUscita)
    {
        lock (_blocco)
        {
            if (_istanteUltimaLettura is not long istante)
                return null;
            TimeSpan trascorso = _tempo.GetElapsedTime(_momentoUltimaLettura);
            return istante + (long)((trascorso - latenzaUscita).TotalMilliseconds * 1000);
        }
    }

    /// <summary>
    /// Misura la pausa dall'arrivo precedente e ricalcola l'obiettivo: la pausa più lunga
    /// dell'ultimo minuto più la tolleranza, tra il minimo e il tetto.
    /// </summary>
    private void AggiornaObiettivo()
    {
        long adesso = _tempo.GetTimestamp();
        if (_ultimoArrivo is long prima)
        {
            int richiesti = Byte(_tempo.GetElapsedTime(prima, adesso)) + _byteTolleranza;
            // Le pause normali (un pacchetto ogni 20 ms) non si memorizzano: non alzerebbero nulla.
            if (richiesti > _byteObiettivoMinimo)
                _pause.Enqueue((adesso, Math.Min(richiesti, _byteTetto)));
        }
        _ultimoArrivo = adesso;

        while (_pause.TryPeek(out var piuVecchia) && _tempo.GetElapsedTime(piuVecchia.Momento, adesso) > FinestraPause)
            _pause.Dequeue();
        _byteObiettivo = _byteObiettivoMinimo;
        foreach (var (_, richiesti) in _pause)
            _byteObiettivo = Math.Max(_byteObiettivo, richiesti);
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
        // Dopo un riempimento il suono entra in dissolvenza invece che di colpo.
        double guadagno = 1;
        if (_entrataFatti < _istantiDissolvenza)
            guadagno = (double)++_entrataFatti / (_istantiDissolvenza + 1);
        // Dopo un taglio i campioni buttati sfumano in quelli nuovi.
        int istanteIncrocio = _incrocioFatti;
        double pesoNuovi = 1;
        if (_incrocioFatti < _incrocioTotali)
            pesoNuovi = (double)++_incrocioFatti / (_incrocioTotali + 1);

        int primo = (_inizio + indice * _byteBlocco) % _anello.Length;
        int secondo = (primo + _byteBlocco) % _anello.Length;
        for (int canale = 0; canale < _canali; canale++)
        {
            double valore = BinaryPrimitives.ReadInt16LittleEndian(_anello.AsSpan(primo + canale * 2));
            if (frazione > 0)
            {
                short b = BinaryPrimitives.ReadInt16LittleEndian(_anello.AsSpan(secondo + canale * 2));
                valore += (b - valore) * frazione;
            }
            valore *= guadagno;
            if (pesoNuovi < 1)
                valore = _vecchi[istanteIncrocio * _canali + canale] * (1 - pesoNuovi) + valore * pesoNuovi;
            BinaryPrimitives.WriteInt16LittleEndian(uscita[(canale * 2)..], (short)Math.Round(valore));
        }
    }

    /// <summary>Prima di un buco gli ultimi campioni scritti scendono a zero invece di interrompersi di colpo.</summary>
    private void SfumaInUscita(Span<byte> destinazione, int scritti)
    {
        int quanti = Math.Min(_istantiDissolvenza, scritti);
        for (int j = 0; j < quanti; j++)
        {
            double guadagno = (double)(quanti - j) / (quanti + 1);
            Span<byte> istante = destinazione.Slice((scritti - quanti + j) * _byteBlocco, _byteBlocco);
            for (int canale = 0; canale < _canali; canale++)
            {
                short valore = BinaryPrimitives.ReadInt16LittleEndian(istante[(canale * 2)..]);
                BinaryPrimitives.WriteInt16LittleEndian(istante[(canale * 2)..], (short)Math.Round(valore * guadagno));
            }
        }
    }

    /// <summary>Copia i campioni che stavano per essere suonati, da far sfumare dopo il taglio.</summary>
    private void PreparaIncrocio()
    {
        _incrocioTotali = Math.Min(_istantiDissolvenza, _disponibili / _byteBlocco);
        _incrocioFatti = 0;
        for (int i = 0; i < _incrocioTotali; i++)
        {
            int posizione = (_inizio + i * _byteBlocco) % _anello.Length;
            for (int canale = 0; canale < _canali; canale++)
                _vecchi[i * _canali + canale] = BinaryPrimitives.ReadInt16LittleEndian(_anello.AsSpan(posizione + canale * 2));
        }
    }

    private void Scarta(int byteDaScartare)
    {
        byteDaScartare = Math.Min(Allinea(byteDaScartare + _byteBlocco - 1), _disponibili);
        _inizio = (_inizio + byteDaScartare) % _anello.Length;
        _disponibili -= byteDaScartare;
        _fase = 0;
        Avanza(byteDaScartare);
    }

    private void Avanza(int byteLetti)
    {
        _letti += byteLetti;
        // Le ancore dei pacchetti ormai suonati per intero non servono più.
        while (_ancore.Count >= 2 && _ancore.ElementAt(1).Posizione <= _letti)
            _ancore.Dequeue();
    }

    /// <summary>Istante di cattura del prossimo campione da leggere.</summary>
    private long IstanteLetto()
    {
        if (!_ancore.TryPeek(out var ancora))
            return 0;
        return ancora.IstanteMicrosecondi + DurataMicrosecondi(_letti - ancora.Posizione);
    }

    private long DurataMicrosecondi(long byteTotali) => byteTotali * 1_000_000 / _byteAlSecondo;

    private TimeSpan Durata(int byteTotali) => TimeSpan.FromSeconds((double)byteTotali / _byteAlSecondo);

    private int Byte(TimeSpan durata) => Allinea((int)(_byteAlSecondo * durata.TotalSeconds));

    private int Allinea(int byteTotali) => byteTotali / _byteBlocco * _byteBlocco;
}
