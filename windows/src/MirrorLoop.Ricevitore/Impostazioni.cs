using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Le scelte di chi usa il ricevitore, in impostazioni.json in %LOCALAPPDATA%\MirrorLoop. Un file
/// illeggibile vale come "nessuna scelta": si torna ai predefiniti.
///
/// Si ricorda se le informazioni (S) e le statistiche (Ctrl+S) erano aperte (chiuse al primo avvio) e il nome con cui il PC compare nell'elenco dell'iPhone (annuncio Bonjour). Il nome
/// di Windows ("DESKTOP-T982V1C") non dice niente a nessuno: si propone "PC di &lt;nome&gt;", come fa
/// l'iPhone con "iPhone di &lt;nome&gt;", e chi vuole lo cambia. Il predefinito non si salva ma si
/// ricalcola a ogni avvio, così segue un eventuale cambio del nome dell'account.
///
/// Gli iPhone riconoscono il PC dal certificato, non dal nome: cambiarlo non rompe gli abbinamenti
/// (sull'iPhone basta scegliere il nome nuovo nell'elenco).
/// </summary>
public sealed unsafe partial class Impostazioni
{
    public const string NomeFile = "impostazioni.json";

    /// <summary>
    /// Il nome dell'istanza è una sola etichetta DNS (RFC 6763, 4.1.1): al massimo 63 byte in UTF-8.
    /// </summary>
    public const int ByteMassimiNome = 63;

    private sealed record Dati(
        [property: JsonPropertyName("nomePC")] string? NomePC,
        [property: JsonPropertyName("statisticheVisibili")] bool StatisticheVisibili = false,
        [property: JsonPropertyName("informazioniVisibili")] bool InformazioniVisibili = false);

    private static readonly JsonSerializerOptions Formato = new() { WriteIndented = true };

    private readonly string _percorso;
    private Dati _dati;

    /// <param name="predefinito">Solo per i test: il nome proposto al posto di quello calcolato dall'account.</param>
    internal Impostazioni(string? cartella = null, string? predefinito = null)
    {
        cartella ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorLoop");
        _percorso = Path.Combine(cartella, NomeFile);
        NomePCPredefinito = predefinito ?? NomePredefinito(NomeCompletoUtente(), Environment.UserName, Environment.MachineName);
        Dati letti = Leggi(_percorso) ?? new Dati(null);
        _dati = letti with { NomePC = letti.NomePC is string nome && Pulisci(nome) is { Length: > 0 } pulito ? pulito : null };
    }

    public Impostazioni() : this(null, null)
    {
    }

    /// <summary>Il nome annunciato: quello scelto, o il predefinito.</summary>
    public string NomePC => _dati.NomePC ?? NomePCPredefinito;

    public string NomePCPredefinito { get; }

    /// <summary>
    /// Salva il nome scelto, ripulito (<see cref="Pulisci"/>). Vuoto o uguale al predefinito vuol dire
    /// "torna al predefinito". Lancia IOException o UnauthorizedAccessException se non si può scrivere.
    /// </summary>
    public void CambiaNomePC(string nome)
    {
        string pulito = Pulisci(nome);
        string? scelto = pulito.Length == 0 || pulito == NomePCPredefinito ? null : pulito;
        Salva(_dati with { NomePC = scelto });
    }

    public bool StatisticheVisibili => _dati.StatisticheVisibili;

    /// <summary>Lancia IOException o UnauthorizedAccessException se non si può scrivere.</summary>
    public void CambiaStatisticheVisibili(bool visibili) => Salva(_dati with { StatisticheVisibili = visibili });

    public bool InformazioniVisibili => _dati.InformazioniVisibili;

    /// <summary>Lancia IOException o UnauthorizedAccessException se non si può scrivere.</summary>
    public void CambiaInformazioniVisibili(bool visibili) => Salva(_dati with { InformazioniVisibili = visibili });

    /// <summary>
    /// Rende il nome adatto all'annuncio: il punto separerebbe le etichette nel nome completo che si
    /// passa a Windows ("nome._mirrorloop._udp.local") e la barra rovesciata è il carattere di escape
    /// di DNS, quindi diventano spazi come i caratteri di controllo; spazi doppi tolti; al massimo
    /// <see cref="ByteMassimiNome"/> byte, senza spezzare un carattere (lettere accentate, emoji).
    /// </summary>
    public static string Pulisci(string nome)
    {
        var testo = new StringBuilder();
        foreach (char c in nome.Normalize(NormalizationForm.FormC))
            testo.Append(c is '.' or '\\' || char.IsControl(c) ? ' ' : c);
        string compatto = string.Join(' ', testo.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var risultato = new StringBuilder();
        int byteUsati = 0;
        TextElementEnumerator elementi = StringInfo.GetTextElementEnumerator(compatto);
        while (elementi.MoveNext())
        {
            string elemento = elementi.GetTextElement();
            byteUsati += Encoding.UTF8.GetByteCount(elemento);
            if (byteUsati > ByteMassimiNome)
                break;
            risultato.Append(elemento);
        }
        return risultato.ToString().TrimEnd();
    }

    /// <summary>
    /// "PC di" (in inglese "…'s PC") e il nome proprio dell'account (la prima parola del nome completo: basta a riconoscerlo
    /// e non annuncia il cognome in rete); senza nome completo, il nome utente; al limite il nome di Windows.
    /// </summary>
    internal static string NomePredefinito(string? nomeCompleto, string utente, string nomeWindows)
    {
        string? nome = nomeCompleto?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(nome))
            nome = utente;
        string proposto = string.IsNullOrWhiteSpace(nome) ? "" : Pulisci(string.Format(Testi.NomePCPredefinito, nome));
        return proposto.Length > 0 ? proposto : Pulisci(nomeWindows);
    }

    /// <summary>
    /// Il nome completo dell'account locale (quello di Impostazioni → Account, per esempio "Andrei Telea"),
    /// o null (account di dominio, errore). NetUserGetInfo al livello 10 non richiede privilegi.
    /// </summary>
    private static string? NomeCompletoUtente()
    {
        UserInfo10* info = null;
        try
        {
            if (NetUserGetInfo(null, Environment.UserName, 10, (byte**)&info) != 0 || info == null)
                return null;
            return Marshal.PtrToStringUni(info->usri10_full_name);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (info != null)
                NetApiBufferFree(info);
        }
    }

    private static Dati? Leggi(string percorso)
    {
        try
        {
            return File.Exists(percorso) ? JsonSerializer.Deserialize<Dati>(File.ReadAllText(percorso)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Prima un file temporaneo, poi la sostituzione: un'interruzione a metà non lascia un file rovinato.</summary>
    private void Salva(Dati dati)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_percorso)!);
        string temporaneo = _percorso + ".nuovo";
        File.WriteAllText(temporaneo, JsonSerializer.Serialize(dati, Formato));
        File.Move(temporaneo, _percorso, overwrite: true);
        _dati = dati;
    }

    // ---- Windows (lmaccess.h, lmapibuf.h) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo10
    {
        public nint usri10_name;
        public nint usri10_comment;
        public nint usri10_usr_comment;
        public nint usri10_full_name;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint NetUserGetInfo(string? servername, string username, uint level, byte** bufptr);

    [LibraryImport("netapi32.dll")]
    private static partial uint NetApiBufferFree(void* buffer);
}
