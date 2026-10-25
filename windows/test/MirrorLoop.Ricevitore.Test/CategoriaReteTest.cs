namespace MirrorLoop.Ricevitore.Test;

public class CategoriaReteTest
{
    [Fact]
    public void LAvvisoCompareSoloPerLeRetiPubbliche()
    {
        Assert.Contains("Pubblica", CategoriaRete.Avviso(CategoriaDiRete.Pubblica));
        Assert.Null(CategoriaRete.Avviso(CategoriaDiRete.Privata));
        Assert.Null(CategoriaRete.Avviso(CategoriaDiRete.Dominio));
        // Lettura non riuscita: meglio nessun avviso che uno sbagliato.
        Assert.Null(CategoriaRete.Avviso(null));
    }

    [Fact]
    public void UnaSchedaSconosciutaNonHaCategoria()
    {
        Assert.Null(CategoriaRete.DellaScheda("non è un GUID"));
        Assert.Null(CategoriaRete.DellaScheda(Guid.Empty.ToString("B")));
    }

    [Fact]
    public void LaReteDiCasaSiLeggeSenzaEccezioni()
    {
        // Dipende dal PC: con una rete collegata Windows conosce la sua categoria.
        CategoriaDiRete? categoria = CategoriaRete.DellaReteDiCasa();
        if (AnnuncioBonjour.InterfacciaDiCasa() is not null)
            Assert.NotNull(categoria);
    }
}
