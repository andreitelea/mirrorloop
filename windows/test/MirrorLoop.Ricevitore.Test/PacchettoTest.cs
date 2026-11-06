namespace MirrorLoop.Ricevitore.Test;

public sealed class PacchettoTest
{
    [Fact]
    public void IlProcessoDeiTestNonGiraDaUnPacchetto()
    {
        // I test girano con dotnet test, fuori da qualsiasi pacchetto MSIX: Windows deve rispondere
        // "nessun pacchetto" e la classe non deve inventarsi un nome.
        Assert.Null(Pacchetto.NomeFamiglia);
        Assert.False(Pacchetto.Impacchettato);
    }
}
