namespace MirrorLoop.Ricevitore.Test;

public sealed class AltraVersioneTest : IDisposable
{
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_localAppData))
            Directory.Delete(_localAppData, recursive: true);
    }

    [Fact]
    public void LaVersioneDelloStoreSiRiconosceDallaCartellaDelPacchetto()
    {
        Assert.False(AltraVersione.StoreInstallata(_localAppData));

        // Altre app nei pacchetti non contano.
        Directory.CreateDirectory(Path.Combine(_localAppData, "Packages", "Microsoft.WindowsCalculator_8wekyb3d8bbwe"));
        Directory.CreateDirectory(Path.Combine(_localAppData, "Packages", "AndreiNicolaeTelea.MirrorLoopAltro_abc"));
        Assert.False(AltraVersione.StoreInstallata(_localAppData));

        Directory.CreateDirectory(Path.Combine(_localAppData, "Packages", "AndreiNicolaeTelea.MirrorLoop_8xyz123"));
        Assert.True(AltraVersione.StoreInstallata(_localAppData));
    }

    [Fact]
    public void LaChiaveDellInstallatoreClassicoUsaLAppIdDiInnoSetup()
    {
        string iss = File.ReadAllText(Path.Combine(CartellaRepository(), "windows", "installatore", "MirrorLoop.iss"));

        // Nel file di Inno Setup la graffa iniziale è raddoppiata: {{D22F…} vuol dire {D22F…}.
        Assert.Contains("AppId={{D22F4D56-ADEC-4FB3-AA9F-78276D9C9332}", iss);
        Assert.EndsWith(@"\{D22F4D56-ADEC-4FB3-AA9F-78276D9C9332}_is1", AltraVersione.ChiaveClassica);
    }

    [Fact]
    public void IlPrefissoDelPacchettoCoincideConIlNomeNelManifest()
    {
        string manifest = File.ReadAllText(Path.Combine(CartellaRepository(), "windows", "msix", "AppxManifest.xml"));

        Assert.Contains($"Name=\"{AltraVersione.PrefissoPacchetto.TrimEnd('_')}\"", manifest);
    }

    // Dal percorso di questo file al momento della compilazione: i test possono girare da una
    // cartella di compilazione fuori dal repository.
    private static string CartellaRepository([System.Runtime.CompilerServices.CallerFilePath] string questoFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(questoFile)!, "..", "..", ".."));
}
