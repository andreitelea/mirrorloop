namespace MirrorLoop.Ricevitore.Test;

public sealed class TelefoniAbbinatiTest : IDisposable
{
    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_cartella))
            Directory.Delete(_cartella, recursive: true);
    }

    private static byte[] Chiave(byte riempimento) => Enumerable.Repeat(riempimento, 65).ToArray();

    [Fact]
    public void UnTelefonoAggiuntoSiRitrovaAncheDopoLaRiapertura()
    {
        var telefoni = new TelefoniAbbinati(_cartella);
        Assert.False(telefoni.Contiene(Chiave(1)));

        telefoni.Aggiungi(Chiave(1), "iPhone");

        Assert.True(telefoni.Contiene(Chiave(1)));
        Assert.False(telefoni.Contiene(Chiave(2)));
        var riaperto = new TelefoniAbbinati(_cartella);
        Assert.True(riaperto.Contiene(Chiave(1)));
        Assert.Equal(1, riaperto.Numero);
    }

    [Fact]
    public void RiabbinareLoStessoTelefonoNonLoDuplica()
    {
        var telefoni = new TelefoniAbbinati(_cartella);
        telefoni.Aggiungi(Chiave(1), "iPhone");
        telefoni.Aggiungi(Chiave(1), "iPhone");
        telefoni.Aggiungi(Chiave(2), "iPhone");

        Assert.Equal(2, new TelefoniAbbinati(_cartella).Numero);
    }

    [Fact]
    public void DimenticaTuttiSvuotaAncheIlFile()
    {
        var telefoni = new TelefoniAbbinati(_cartella);
        telefoni.Aggiungi(Chiave(1), "iPhone");

        telefoni.DimenticaTutti();

        Assert.False(telefoni.Contiene(Chiave(1)));
        Assert.Equal(0, new TelefoniAbbinati(_cartella).Numero);
    }

    [Fact]
    public void UnFileRovinatoValeComeElencoVuoto()
    {
        Directory.CreateDirectory(_cartella);
        File.WriteAllText(Path.Combine(_cartella, TelefoniAbbinati.NomeFile), "{ non è json");

        var telefoni = new TelefoniAbbinati(_cartella);

        Assert.Equal(0, telefoni.Numero);
        telefoni.Aggiungi(Chiave(3), "iPhone");
        Assert.True(new TelefoniAbbinati(_cartella).Contiene(Chiave(3)));
    }
}
