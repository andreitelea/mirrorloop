using System.IO;
using Microsoft.Win32;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Trova l'altra versione di MirrorLoop sullo stesso PC: quella dell'installatore del sito (classica)
/// o quella del Microsoft Store. Le due non possono essere aperte insieme (la porta UDP 7450 è una
/// sola) e il plugin per OBS messo dalla classica la versione dello Store non lo può aggiornare:
/// meglio tenerne una sola.
/// </summary>
public static class AltraVersione
{
    public enum Quale
    {
        Nessuna,
        /// <summary>Installata con l'installatore del sito (Inno Setup).</summary>
        Classica,
        Store,
    }

    // Inno Setup registra la disinstallazione sotto l'AppId di windows/installatore/MirrorLoop.iss
    // seguito da "_is1" (verificato sul PC di Andre il 06/11/2026). Con PrivilegesRequired=admin
    // sta in HKLM.
    internal const string ChiaveClassica =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{D22F4D56-ADEC-4FB3-AA9F-78276D9C9332}_is1";

    // Il nome del pacchetto assegnato da Partner Center (windows/msix/AppxManifest.xml): Windows
    // crea %LOCALAPPDATA%\Packages\<nome>_<hash> per ogni utente che lo installa e la toglie
    // quando lo disinstalla.
    internal const string PrefissoPacchetto = "AndreiNicolaeTelea.MirrorLoop_";

    /// <summary>L'altra versione installata, vista da questa.</summary>
    public static Quale Trova()
    {
        try
        {
            if (Pacchetto.Impacchettato)
                return ClassicaInstallata() ? Quale.Classica : Quale.Nessuna;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return StoreInstallata(localAppData) ? Quale.Store : Quale.Nessuna;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Solo un avviso: se non si riesce a guardare, non lo si mostra.
            return Quale.Nessuna;
        }
    }

    private static bool ClassicaInstallata()
    {
        using RegistryKey? chiave = Registry.LocalMachine.OpenSubKey(ChiaveClassica);
        return chiave is not null;
    }

    internal static bool StoreInstallata(string localAppData)
    {
        string pacchetti = Path.Combine(localAppData, "Packages");
        return Directory.Exists(pacchetti) && Directory.EnumerateDirectories(pacchetti, PrefissoPacchetto + "*").Any();
    }
}
