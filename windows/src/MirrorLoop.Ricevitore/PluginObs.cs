using System.IO;
using System.Security.Cryptography;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Il plugin per OBS nella versione del Microsoft Store. L'installatore classico lo mette da solo
/// nella cartella dei plugin di OBS; il pacchetto MSIX invece se lo porta dietro (cartella
/// obs-plugin\mirrorloop) e lo copia il ricevitore, quando l'utente lo chiede.
///
/// La destinazione è C:\ProgramData\obs-studio\plugins\mirrorloop, la stessa dell'installatore
/// classico (OBS la legge per tutti gli utenti). Non serve essere amministratori: in ProgramData
/// gli utenti possono creare cartelle e file nuovi (letto con icacls il 06/11/2026), e un pacchetto
/// MSIX scrive fuori da AppData come un programma qualsiasi. Non vale il contrario: i file messi
/// dall'installatore classico sono dell'amministratore, e il ricevitore non li può sostituire.
/// </summary>
public sealed class PluginObs
{
    public enum Stato
    {
        /// <summary>Tutti i file ci sono e sono uguali a quelli del pacchetto.</summary>
        Aggiornato,
        Mancante,
        /// <summary>C'è, ma qualche file è diverso (di solito una versione vecchia).</summary>
        Diverso,
    }

    public enum Esito
    {
        Riuscito,
        /// <summary>Un file è aperto da un altro programma: OBS, che tiene caricato il plugin.</summary>
        Occupato,
        /// <summary>Mancano i permessi: file dell'installatore classico, messi da un amministratore.</summary>
        NonPermesso,
        Errore,
    }

    private readonly string _sorgente;

    /// <param name="sorgente">La cartella "mirrorloop" del plugin dentro il pacchetto.</param>
    /// <param name="destinazione">La cartella "mirrorloop" tra i plugin di OBS.</param>
    /// <param name="cartellaObsUtente">La cartella delle impostazioni di OBS dell'utente (c'è se OBS è stato aperto almeno una volta).</param>
    internal PluginObs(string sorgente, string destinazione, string cartellaObsUtente)
    {
        _sorgente = sorgente;
        Destinazione = destinazione;
        ObsUsato = Directory.Exists(cartellaObsUtente) || Directory.Exists(Path.GetDirectoryName(destinazione));
        Impronta = CalcolaImpronta(sorgente);
    }

    /// <summary>Il plugin di questo pacchetto, o null fuori dal pacchetto (versione classica) o se il pacchetto non lo contiene.</summary>
    public static PluginObs? DelPacchetto()
    {
        if (!Pacchetto.Impacchettato)
            return null;
        string sorgente = Path.Combine(AppContext.BaseDirectory, "obs-plugin", "mirrorloop");
        if (!Directory.Exists(sorgente))
            return null;
        string destinazione = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obs-studio", "plugins", "mirrorloop");
        string cartellaObsUtente = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
        return new PluginObs(sorgente, destinazione, cartellaObsUtente);
    }

    public string Destinazione { get; }

    /// <summary>
    /// Vero se OBS sembra installato o usato su questo PC: solo allora conviene proporre il plugin
    /// da solo all'avvio (con il tasto P si può installare comunque).
    /// </summary>
    public bool ObsUsato { get; }

    /// <summary>
    /// Riassume i file del plugin del pacchetto: cambia con la versione. Serve a ricordare "Non ora"
    /// solo per questa versione del plugin.
    /// </summary>
    public string Impronta { get; }

    public Stato Controlla()
    {
        if (!Directory.Exists(Destinazione))
            return Stato.Mancante;
        foreach (string file in FileDelPlugin(_sorgente))
        {
            string installato = Path.Combine(Destinazione, Path.GetRelativePath(_sorgente, file));
            try
            {
                if (!File.Exists(installato))
                    return Stato.Diverso;
                if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(installato)))
                    return Stato.Diverso;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non si riesce a leggerlo (per esempio OBS lo tiene aperto in modo esclusivo):
                // meglio proporre di nuovo che tacere.
                return Stato.Diverso;
            }
        }
        return Stato.Aggiornato;
    }

    /// <summary>Copia tutti i file, sostituendo quelli che ci sono. La DLL per ultima: OBS la carica solo se c'è.</summary>
    public Esito Installa()
    {
        try
        {
            foreach (string file in FileDelPlugin(_sorgente).OrderBy(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                string installato = Path.Combine(Destinazione, Path.GetRelativePath(_sorgente, file));
                Directory.CreateDirectory(Path.GetDirectoryName(installato)!);
                File.Copy(file, installato, overwrite: true);
            }
            return Esito.Riuscito;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Classifica(ex);
        }
    }

    /// <summary>Toglie la cartella del plugin da OBS (Windows non lo fa quando si disinstalla il pacchetto).</summary>
    public Esito Rimuovi()
    {
        try
        {
            if (Directory.Exists(Destinazione))
                Directory.Delete(Destinazione, recursive: true);
            return Esito.Riuscito;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Classifica(ex);
        }
    }

    /// <summary>L'ultimo errore sconosciuto, da mostrare a chi chiede aiuto.</summary>
    public string? UltimoErrore { get; private set; }

    private Esito Classifica(Exception ex)
    {
        switch (ex)
        {
            case UnauthorizedAccessException:
                return Esito.NonPermesso;
            // ERROR_SHARING_VIOLATION (32) e ERROR_LOCK_VIOLATION (33) di winerror.h.
            case IOException io when (io.HResult & 0xFFFF) is 32 or 33:
                return Esito.Occupato;
            default:
                UltimoErrore = ex.Message;
                return Esito.Errore;
        }
    }

    private static IEnumerable<string> FileDelPlugin(string cartella) =>
        Directory.EnumerateFiles(cartella, "*", SearchOption.AllDirectories);

    private static string CalcolaImpronta(string cartella)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in FileDelPlugin(cartella).Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(cartella, file).ToLowerInvariant()));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }
}
