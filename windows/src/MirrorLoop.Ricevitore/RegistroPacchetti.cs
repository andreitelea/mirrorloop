using System.Globalization;
using System.IO;
using System.Text;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Registro su file (CSV) di ogni pacchetto ricevuto, per analizzare a posteriori ritardi e pause.
/// Si accende e si spegne dalla finestra; il file va in %LOCALAPPDATA%\MirrorLoop, fuori dal repository.
///
/// Colonne:
/// - tipo: "v" (video) o "a" (audio);
/// - istante_iphone_us: istante di cattura, con l'orologio dell'iPhone;
/// - arrivo_pc_us: arrivo sul PC, con l'orologio del PC;
/// - scarto_us: scarto tra gli orologi stimato in quel momento (vuoto se non ancora sincronizzati);
///   istante di cattura in ora del PC = istante_iphone_us − scarto_us;
/// - byte: dimensione del contenuto;
/// - keyframe: 1 per i keyframe video.
///
/// Thread-safe: scrive il thread di rete, accende e spegne la finestra.
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
            _file.WriteLine("tipo,istante_iphone_us,arrivo_pc_us,scarto_us,byte,keyframe");
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
                $"{tipo},{istanteIphone},{arrivoPc},{scarto},{dimensione},{(keyframe ? 1 : 0)}"));
            _righe++;
        }
    }

    public void Dispose() => Ferma();
}
