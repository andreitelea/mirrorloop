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
