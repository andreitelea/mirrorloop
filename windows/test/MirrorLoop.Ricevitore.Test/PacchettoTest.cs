namespace MirrorLoop.Ricevitore.Test;

public sealed class PacchettoTest : IDisposable
{
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_localAppData))
            Directory.Delete(_localAppData, recursive: true);
    }

    [Fact]
    public void IlProcessoDeiTestNonGiraDaUnPacchetto()
    {
        // I test girano con dotnet test, fuori da qualsiasi pacchetto MSIX: Windows deve rispondere
        // "nessun pacchetto" e la classe non deve inventarsi un nome.
        Assert.Null(Pacchetto.NomeFamiglia);
        Assert.False(Pacchetto.Impacchettato);
    }

    private string Registro => Path.Combine(_localAppData, "MirrorLoop", "registro-1.csv");

    private string Privato => Path.Combine(_localAppData, "Packages", "MirrorLoop.Prova_abc", "LocalCache", "Local", "MirrorLoop", "registro-1.csv");

    [Fact]
    public void FuoriDalPacchettoIlPercorsoResta()
    {
        Assert.Equal(Registro, Pacchetto.PercorsoVisibile(Registro, null, _localAppData));
    }

    [Fact]
    public void NelPacchettoSiIndicaLaCartellaPrivataSeIlFileStaLi()
    {
        Assert.Equal(Registro, Pacchetto.PercorsoVisibile(Registro, "MirrorLoop.Prova_abc", _localAppData));

        Directory.CreateDirectory(Path.GetDirectoryName(Privato)!);
        File.WriteAllText(Privato, "tipo");

        Assert.Equal(Privato, Pacchetto.PercorsoVisibile(Registro, "MirrorLoop.Prova_abc", _localAppData));
    }

    [Fact]
    public void UnPercorsoFuoriDaLocalAppDataNonSiTocca()
    {
        string altrove = Path.Combine(Path.GetTempPath(), "altrove", "registro.csv");

        Assert.Equal(altrove, Pacchetto.PercorsoVisibile(altrove, "MirrorLoop.Prova_abc", _localAppData));
    }
}
