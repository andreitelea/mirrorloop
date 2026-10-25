using System.Text;

namespace MirrorLoop.Ricevitore.Test;

public sealed class ImpostazioniTest : IDisposable
{
    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_cartella))
            Directory.Delete(_cartella, recursive: true);
    }

    [Fact]
    public void SenzaSceltaValeIlPredefinito()
    {
        var impostazioni = new Impostazioni(_cartella, "PC di Andrei");

        Assert.Equal("PC di Andrei", impostazioni.NomePC);
        Assert.False(File.Exists(Path.Combine(_cartella, Impostazioni.NomeFile)));
    }

    [Fact]
    public void IlNomeSceltoSiRitrovaAncheDopoLaRiapertura()
    {
        new Impostazioni(_cartella, "PC di Andrei").CambiaNomePC("  PC dello   studio ");

        var riaperte = new Impostazioni(_cartella, "PC di Andrei");
        Assert.Equal("PC dello studio", riaperte.NomePC);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PC di Andrei")]
    public void VuotoOUgualeAlPredefinitoTornaAlPredefinito(string nome)
    {
        var impostazioni = new Impostazioni(_cartella, "PC di Andrei");
        impostazioni.CambiaNomePC("Studio");

        impostazioni.CambiaNomePC(nome);

        Assert.Equal("PC di Andrei", impostazioni.NomePC);
        // Il predefinito non si salva: se cambia (account rinominato) lo si segue.
        Assert.Equal("PC di Marco", new Impostazioni(_cartella, "PC di Marco").NomePC);
    }

    [Fact]
    public void LeStatisticheSonoChiuseAlPrimoAvvioEPoiSiRicordanoInsiemeAlNome()
    {
        var impostazioni = new Impostazioni(_cartella, "PC di Andrei");
        Assert.False(impostazioni.StatisticheVisibili);

        impostazioni.CambiaStatisticheVisibili(true);
        impostazioni.CambiaNomePC("Studio");

        var riaperte = new Impostazioni(_cartella, "PC di Andrei");
        Assert.True(riaperte.StatisticheVisibili);
        Assert.Equal("Studio", riaperte.NomePC);
    }

    [Fact]
    public void UnFileRovinatoValeComeNessunaScelta()
    {
        Directory.CreateDirectory(_cartella);
        File.WriteAllText(Path.Combine(_cartella, Impostazioni.NomeFile), "{ non è json");

        var impostazioni = new Impostazioni(_cartella, "PC di Andrei");

        Assert.Equal("PC di Andrei", impostazioni.NomePC);
        impostazioni.CambiaNomePC("Studio");
        Assert.Equal("Studio", new Impostazioni(_cartella, "PC di Andrei").NomePC);
    }

    [Theory]
    [InlineData("PC.di.casa", "PC di casa")]
    [InlineData("PC\\casa", "PC casa")]
    [InlineData("PC\tdi\ncasa", "PC di casa")]
    [InlineData("  Studio  ", "Studio")]
    [InlineData("Andrè", "Andrè")]
    public void PulisciTogliCiòCheRomperebbeLAnnuncio(string nome, string atteso) =>
        Assert.Equal(atteso, Impostazioni.Pulisci(nome));

    [Fact]
    public void PulisciTagliaA63ByteSenzaSpezzareICaratteri()
    {
        // "è" sono 2 byte in UTF-8, l'emoji 4: il taglio cade a metà di un carattere se non si sta attenti.
        string lungo = new string('a', 60) + "è" + "🙂" + "fine";

        string pulito = Impostazioni.Pulisci(lungo);

        Assert.Equal(new string('a', 60) + "è", pulito);
        Assert.True(Encoding.UTF8.GetByteCount(pulito) <= Impostazioni.ByteMassimiNome);
    }

    [Theory]
    [InlineData("Andrei Telea", "Andrei", "DESKTOP-T982V1C", "PC di Andrei")]
    [InlineData(null, "Andrei", "DESKTOP-T982V1C", "PC di Andrei")]
    [InlineData("  ", "marco", "DESKTOP-T982V1C", "PC di marco")]
    [InlineData(null, "", "DESKTOP-T982V1C", "DESKTOP-T982V1C")]
    public void IlPredefinitoUsaIlNomeProprioDellAccount(string? nomeCompleto, string utente, string nomeWindows, string atteso) =>
        Assert.Equal(atteso, Impostazioni.NomePredefinito(nomeCompleto, utente, nomeWindows));
}
