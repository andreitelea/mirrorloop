namespace MirrorLoop.Ricevitore.Test;

public sealed class PluginObsTest : IDisposable
{
    private readonly string _radice = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());
    private readonly string _sorgente;
    private readonly string _plugins;
    private readonly string _destinazione;
    private readonly string _obsUtente;

    public PluginObsTest()
    {
        // Come nel pacchetto: obs-plugin\mirrorloop con la DLL, i testi e la licenza.
        _sorgente = Path.Combine(_radice, "pacchetto", "obs-plugin", "mirrorloop");
        Scrivi(Path.Combine(_sorgente, "bin", "64bit", "mirrorloop.dll"), "dll versione 1");
        Scrivi(Path.Combine(_sorgente, "data", "locale", "it-IT.ini"), "MirrorLoop=\"MirrorLoop\"");
        Scrivi(Path.Combine(_sorgente, "LICENSE-GPL-2.0.txt"), "GPL");
        _plugins = Path.Combine(_radice, "ProgramData", "obs-studio", "plugins");
        _destinazione = Path.Combine(_plugins, "mirrorloop");
        _obsUtente = Path.Combine(_radice, "AppData", "obs-studio");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_radice))
            return;
        foreach (string file in Directory.EnumerateFiles(_radice, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_radice, recursive: true);
    }

    private static void Scrivi(string percorso, string testo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
        File.WriteAllText(percorso, testo);
    }

    private PluginObs Plugin() => new(_sorgente, _destinazione, _obsUtente);

    private string DllInstallata => Path.Combine(_destinazione, "bin", "64bit", "mirrorloop.dll");

    [Fact]
    public void SenzaCartellaManca()
    {
        Assert.Equal(PluginObs.Stato.Mancante, Plugin().Controlla());
    }

    [Fact]
    public void DopoLInstallazioneTuttiIFileSonoAlLoroPostoEUguali()
    {
        PluginObs plugin = Plugin();

        Assert.Equal(PluginObs.Esito.Riuscito, plugin.Installa());

        Assert.Equal(PluginObs.Stato.Aggiornato, plugin.Controlla());
        Assert.Equal("dll versione 1", File.ReadAllText(DllInstallata));
        Assert.True(File.Exists(Path.Combine(_destinazione, "data", "locale", "it-IT.ini")));
        Assert.True(File.Exists(Path.Combine(_destinazione, "LICENSE-GPL-2.0.txt")));
    }

    [Fact]
    public void UnFileDiversoOMancanteVaAggiornato()
    {
        PluginObs plugin = Plugin();
        plugin.Installa();

        File.WriteAllText(DllInstallata, "dll versione 0");
        Assert.Equal(PluginObs.Stato.Diverso, plugin.Controlla());

        plugin.Installa();
        File.Delete(Path.Combine(_destinazione, "LICENSE-GPL-2.0.txt"));
        Assert.Equal(PluginObs.Stato.Diverso, plugin.Controlla());

        Assert.Equal(PluginObs.Esito.Riuscito, plugin.Installa());
        Assert.Equal(PluginObs.Stato.Aggiornato, plugin.Controlla());
    }

    [Fact]
    public void LaRimozioneTogliePropriTuttoEConLaCartellaGiaVuotaNonSbaglia()
    {
        PluginObs plugin = Plugin();
        plugin.Installa();

        Assert.Equal(PluginObs.Esito.Riuscito, plugin.Rimuovi());
        Assert.False(Directory.Exists(_destinazione));
        Assert.Equal(PluginObs.Stato.Mancante, plugin.Controlla());
        Assert.Equal(PluginObs.Esito.Riuscito, plugin.Rimuovi());
    }

    [Fact]
    public void ConLaDllApertaDaOBSLaCopiaRisultaOccupata()
    {
        PluginObs plugin = Plugin();
        plugin.Installa();
        File.WriteAllText(Path.Combine(_sorgente, "bin", "64bit", "mirrorloop.dll"), "dll versione 2");

        // OBS tiene la DLL caricata: Windows non lascia sostituirla.
        using (new FileStream(DllInstallata, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(PluginObs.Esito.Occupato, plugin.Installa());

        Assert.Equal(PluginObs.Esito.Riuscito, plugin.Installa());
        Assert.Equal("dll versione 2", File.ReadAllText(DllInstallata));
    }

    [Fact]
    public void UnFileCheNonSiPuoSostituireRisultaNonPermesso()
    {
        PluginObs plugin = Plugin();
        plugin.Installa();
        File.WriteAllText(Path.Combine(_sorgente, "LICENSE-GPL-2.0.txt"), "GPL nuova");
        // Un file di sola lettura dà lo stesso errore di un file dell'amministratore (accesso negato).
        File.SetAttributes(Path.Combine(_destinazione, "LICENSE-GPL-2.0.txt"), FileAttributes.ReadOnly);

        Assert.Equal(PluginObs.Esito.NonPermesso, plugin.Installa());
    }

    [Fact]
    public void LImprontaCambiaSoloSeCambiaIlPlugin()
    {
        string prima = Plugin().Impronta;
        Assert.Equal(prima, Plugin().Impronta);

        File.WriteAllText(Path.Combine(_sorgente, "bin", "64bit", "mirrorloop.dll"), "dll versione 2");
        Assert.NotEqual(prima, Plugin().Impronta);
    }

    [Fact]
    public void OBSRisultaUsatoSeCeLaSuaCartellaOAltriPlugin()
    {
        Assert.False(Plugin().ObsUsato);

        Directory.CreateDirectory(_plugins);
        Assert.True(Plugin().ObsUsato);

        Directory.Delete(_plugins);
        Directory.CreateDirectory(_obsUtente);
        Assert.True(Plugin().ObsUsato);
    }

    [Fact]
    public void FuoriDalPacchettoNonCeNessunPlugin()
    {
        // I test girano fuori da un pacchetto MSIX, come la versione classica.
        Assert.Null(PluginObs.DelPacchetto());
    }
}
