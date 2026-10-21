using System.Globalization;
using System.IO;
using System.Text;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Registro su file (CSV) di ogni pacchetto ricevuto, e dei buchi e tagli della riserva audio, per
/// analizzare a posteriori ritardi e pause.
/// Si accende e si spegne dalla finestra; il file va in %LOCALAPPDATA%\MirrorLoop, fuori dal repository.
///
/// Colonne:
/// - tipo: "v" (video), "a" (audio); per la riserva audio "b" (buco), "t" (taglio mentre si suona),
///   "r" (taglio mentre si aspetta di ripartire dopo un buco);
/// - istante_iphone_us: istante di cattura, con l'orologio dell'iPhone (vuoto per b, t, r);
/// - arrivo_pc_us: arrivo sul PC, con l'orologio del PC (per b, t, r: il momento dell'evento);
/// - scarto_us: scarto tra gli orologi stimato in quel momento (vuoto se non ancora sincronizzati);
///   istante di cattura in ora del PC = istante_iphone_us − scarto_us;
/// - byte: dimensione del contenuto (b: silenzio scritto in quella lettura; t, r: byte buttati);
/// - keyframe: 1 per i keyframe video;
/// - riserva_byte, obiettivo_byte: riserva subito prima dell'evento e obiettivo (solo b, t, r);
/// - lettura_byte: quanto ha chiesto la scheda audio in quella lettura (b) o nell'ultima (t, r).
///
/// Thread-safe: scrivono il thread di rete e quello audio, accende e spegne la finestra.
/// </summary>
public sealed class RegistroPacchetti(string? cartella = null) : IDisposable
{
    private readonly string _cartella = cartella
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorLoop");

    private readonly object _blocco = new();
    private StreamWriter? _file;
    private long _righe;

    public string? Percorso { get; private set; }

    public bool Attivo
    {
        get
        {
            lock (_blocco)
                return _file is not null;
        }
    }

    public long Righe => Interlocked.Read(ref _righe);

    public void Avvia()
    {
        lock (_blocco)
        {
            if (_file is not null)
                return;
            Directory.CreateDirectory(_cartella);
            Percorso = Path.Combine(_cartella, $"registro-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.csv");
            _file = new StreamWriter(Percorso, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _file.WriteLine("tipo,istante_iphone_us,arrivo_pc_us,scarto_us,byte,keyframe,riserva_byte,obiettivo_byte,lettura_byte");
            Interlocked.Exchange(ref _righe, 0);
        }
    }

    public void Ferma()
    {
        lock (_blocco)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public void Scrivi(char tipo, long istanteIphone, long arrivoPc, long? scarto, int dimensione, bool keyframe)
    {
        lock (_blocco)
        {
            if (_file is null)
                return;
            _file.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{tipo},{istanteIphone},{arrivoPc},{scarto},{dimensione},{(keyframe ? 1 : 0)},,,"));
            _righe++;
        }
    }

    public void ScriviRiserva(RiservaAudio.Evento evento, long momentoPc)
    {
        char tipo = evento.Tipo switch
        {
            RiservaAudio.TipoEvento.Buco => 'b',
            RiservaAudio.TipoEvento.Taglio => 't',
            _ => 'r',
        };
        lock (_blocco)
        {
            if (_file is null)
                return;
            _file.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{tipo},,{momentoPc},,{evento.ByteCoinvolti},0,{evento.ByteRiserva},{evento.ByteObiettivo},{evento.ByteLettura}"));
            _righe++;
        }
    }

    public void Dispose() => Ferma();
}
