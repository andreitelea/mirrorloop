using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Gli iPhone abbinati a questo PC, riconosciuti dalla loro chiave pubblica (docs/protocollo.md,
/// "Abbinamento"). Il file è in %LOCALAPPDATA%\MirrorLoop, fuori dal repository.
///
/// Un file illeggibile vale come elenco vuoto: al peggio il telefono chiede di nuovo il codice.
/// Thread-safe: legge il thread di rete, scrivono la rete (nuovo abbinamento) e la finestra (dimentica).
/// </summary>
public sealed class TelefoniAbbinati
{
    public const string NomeFile = "telefoni-abbinati.json";

    public sealed record Telefono(
        [property: JsonPropertyName("chiave")] string ChiaveBase64,
        [property: JsonPropertyName("nome")] string Nome,
        [property: JsonPropertyName("abbinatoIl")] DateTimeOffset AbbinatoIl);

    private static readonly JsonSerializerOptions Formato = new() { WriteIndented = true };

    private readonly object _blocco = new();
    private readonly string _percorso;
    private readonly List<Telefono> _telefoni;

    public TelefoniAbbinati(string? cartella = null)
    {
        cartella ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorLoop");
        _percorso = Path.Combine(cartella, NomeFile);
        _telefoni = Leggi(_percorso);
    }

    public int Numero
    {
        get { lock (_blocco) return _telefoni.Count; }
    }

    public bool Contiene(byte[] chiave)
    {
        string cercata = Convert.ToBase64String(chiave);
        lock (_blocco)
            return _telefoni.Exists(t => t.ChiaveBase64 == cercata);
    }

    /// <summary>Aggiunge il telefono (o ne aggiorna nome e data, se c'era già) e salva.</summary>
    public void Aggiungi(byte[] chiave, string nome)
    {
        string base64 = Convert.ToBase64String(chiave);
        lock (_blocco)
        {
            _telefoni.RemoveAll(t => t.ChiaveBase64 == base64);
            _telefoni.Add(new Telefono(base64, nome, DateTimeOffset.Now));
            Salva();
        }
    }

    public void DimenticaTutti()
    {
        lock (_blocco)
        {
            _telefoni.Clear();
            Salva();
        }
    }

    private static List<Telefono> Leggi(string percorso)
    {
        try
        {
            return File.Exists(percorso)
                ? JsonSerializer.Deserialize<List<Telefono>>(File.ReadAllText(percorso)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Prima un file temporaneo, poi la sostituzione: un'interruzione a metà non lascia un file rovinato.</summary>
    private void Salva()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_percorso)!);
        string temporaneo = _percorso + ".nuovo";
        File.WriteAllText(temporaneo, JsonSerializer.Serialize(_telefoni, Formato));
        File.Move(temporaneo, _percorso, overwrite: true);
    }
}
