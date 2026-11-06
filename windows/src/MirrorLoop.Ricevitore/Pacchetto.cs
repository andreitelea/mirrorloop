using System.IO;
using System.Runtime.InteropServices;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Dice se il ricevitore gira da un pacchetto MSIX (la versione del Microsoft Store) o è stato
/// installato con l'installatore classico. Le due versioni sono lo stesso programma, ma quella nel
/// pacchetto non ha un installatore che metta il plugin di OBS al suo posto: lo fa il ricevitore.
/// </summary>
public static unsafe partial class Pacchetto
{
    // winerror.h del Windows SDK 10.0.26100. Fuori da un pacchetto la risposta è
    // APPMODEL_ERROR_NO_PACKAGE (15700).
    private const int Riuscito = 0;
    private const int ErroreBufferInsufficiente = 122;

    private static readonly Lazy<string?> Famiglia = new(LeggiFamiglia);

    /// <summary>
    /// Il nome della famiglia del pacchetto (per esempio "MirrorLoop.Prova_022xar7ycafcy"), o null
    /// se il ricevitore non gira da un pacchetto.
    /// </summary>
    public static string? NomeFamiglia => Famiglia.Value;

    public static bool Impacchettato => NomeFamiglia is not null;

    /// <summary>
    /// Dove si trova davvero un file che il ricevitore ha scritto in %LOCALAPPDATA%, per dirlo a chi
    /// lo cerca con Esplora file (i registri di Ctrl+R).
    /// </summary>
    public static string PercorsoVisibile(string percorso) =>
        PercorsoVisibile(percorso, NomeFamiglia, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// In un pacchetto, i file nuovi scritti in %LOCALAPPDATA% finiscono in una cartella privata del
    /// pacchetto (…\Packages\&lt;famiglia&gt;\LocalCache\Local\…), che il programma vede al posto di
    /// quella vera ma Esplora file no. Succede solo se la cartella MirrorLoop non esisteva già (chi
    /// arriva dalla versione classica usa quella vera: provato il 06/11/2026). Se il file c'è nella
    /// cartella privata si indica quella, altrimenti il percorso com'è.
    /// </summary>
    internal static string PercorsoVisibile(string percorso, string? famiglia, string localAppData)
    {
        if (famiglia is null)
            return percorso;
        string relativo = Path.GetRelativePath(localAppData, percorso);
        if (relativo.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativo))
            return percorso;
        string privato = Path.Combine(localAppData, "Packages", famiglia, "LocalCache", "Local", relativo);
        return File.Exists(privato) ? privato : percorso;
    }

    private static string? LeggiFamiglia()
    {
        try
        {
            uint lunghezza = 0;
            int esito = GetCurrentPackageFamilyName(&lunghezza, null);
            // Con un pacchetto la prima chiamata chiede solo quanto spazio serve.
            if (esito != ErroreBufferInsufficiente)
                return null;

            // La lunghezza comprende il carattere nullo finale.
            char* nome = stackalloc char[(int)lunghezza];
            if (GetCurrentPackageFamilyName(&lunghezza, nome) != Riuscito)
                return null;
            return new string(nome, 0, (int)lunghezza - 1);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    // ---- Windows (appmodel.h) ----

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFamilyName(uint* packageFamilyNameLength, char* packageFamilyName);
}
