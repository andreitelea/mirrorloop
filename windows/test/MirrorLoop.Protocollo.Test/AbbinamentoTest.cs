using System.Security.Cryptography;
using System.Text;

namespace MirrorLoop.Protocollo.Test;

public class AbbinamentoTest
{
    // I valori dell'esempio di docs/protocollo.md ("Abbinamento", "Esempio con valori fissi").
    private static readonly byte[] PrivataEsempio = SHA256.HashData(Encoding.ASCII.GetBytes("MirrorLoop chiave di prova"));
    private static readonly byte[] ImprontaEsempio = SHA256.HashData(Encoding.ASCII.GetBytes("certificato di prova"));
    private static readonly byte[] NumeroPcEsempio = Enumerable.Repeat((byte)0x11, 32).ToArray();
    private static readonly byte[] NumeroIphoneEsempio = Enumerable.Repeat((byte)0x22, 32).ToArray();

    private static ECDsa ChiaveEsempio() =>
        ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = PrivataEsempio });

    [Fact]
    public void LEsempioDelDocumentoTorna()
    {
        using var privata = ChiaveEsempio();
        byte[] chiave = Abbinamento.ChiavePubblica(privata);

        Assert.Equal("0402e1ecbb59b6a867788ad406194e9d186d62a8ea3ae31b441e5df2cdbeb5074647f2ee5bcdd4948f2748373ace3a10fa44233730c2d4353437edbf6ae98504f5",
            Convert.ToHexStringLower(chiave));
        Assert.Equal("45bd8dfef7caf4232b2c31bfc249e6a5dd1743c727291408d2f0a05ebe0db347", Convert.ToHexStringLower(ImprontaEsempio));
        Assert.Equal("ba04f4ce2586799326edcb32db1e6e33dfc33e22534f243e167387de0f8e6c5e",
            Convert.ToHexStringLower(Abbinamento.Impegno(chiave, NumeroIphoneEsempio)));
        Assert.Equal("713021", Abbinamento.Codice(ImprontaEsempio, chiave, NumeroPcEsempio, NumeroIphoneEsempio));

        byte[] firmaDelDocumento = Convert.FromHexString(
            "e754f9bd671aaa7774779c04e57639fea1c9080b5d966ebc5a39641d7e607eb4be622936d4a8e52527dddbe66b7da6fd7d4817d30f047b6a62521dfc8f5191f9");
        Assert.True(Abbinamento.FirmaValida(chiave, ImprontaEsempio, NumeroPcEsempio, NumeroIphoneEsempio, firmaDelDocumento));
    }

    [Fact]
    public void UnaFirmaAppenaFattaEValida()
    {
        using var privata = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] numeroPc = Abbinamento.NuovoNumero(), numeroIphone = Abbinamento.NuovoNumero();
        byte[] firma = Abbinamento.Firma(privata, ImprontaEsempio, numeroPc, numeroIphone);

        Assert.Equal(Abbinamento.LunghezzaFirma, firma.Length);
        Assert.True(Abbinamento.FirmaValida(Abbinamento.ChiavePubblica(privata), ImprontaEsempio, numeroPc, numeroIphone, firma));
    }

    [Fact]
    public void UnaFirmaSullImprontaDiUnImpostoreNonValePerIlPcVero()
    {
        // L'iPhone ha visto il certificato dell'impostore e ha firmato quella impronta.
        using var privata = ChiaveEsempio();
        byte[] improntaImpostore = SHA256.HashData(Encoding.ASCII.GetBytes("certificato dell'impostore"));
        byte[] firma = Abbinamento.Firma(privata, improntaImpostore, NumeroPcEsempio, NumeroIphoneEsempio);

        Assert.False(Abbinamento.FirmaValida(Abbinamento.ChiavePubblica(privata), ImprontaEsempio, NumeroPcEsempio, NumeroIphoneEsempio, firma));
    }

    [Fact]
    public void UnaFirmaNonValeConAltriNumeriOAltreChiavi()
    {
        using var privata = ChiaveEsempio();
        using var altra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] chiave = Abbinamento.ChiavePubblica(privata);
        byte[] firma = Abbinamento.Firma(privata, ImprontaEsempio, NumeroPcEsempio, NumeroIphoneEsempio);

        // Numeri scambiati (una firma vecchia riusata con una sfida nuova).
        Assert.False(Abbinamento.FirmaValida(chiave, ImprontaEsempio, NumeroIphoneEsempio, NumeroPcEsempio, firma));
        Assert.False(Abbinamento.FirmaValida(chiave, ImprontaEsempio, Abbinamento.NuovoNumero(), NumeroIphoneEsempio, firma));
        Assert.False(Abbinamento.FirmaValida(Abbinamento.ChiavePubblica(altra), ImprontaEsempio, NumeroPcEsempio, NumeroIphoneEsempio, firma));

        byte[] alterata = (byte[])firma.Clone();
        alterata[10] ^= 1;
        Assert.False(Abbinamento.FirmaValida(chiave, ImprontaEsempio, NumeroPcEsempio, NumeroIphoneEsempio, alterata));
    }

    [Fact]
    public void UnaChiaveFuoriDallaCurvaValeComeFirmaSbagliataSenzaEccezioni()
    {
        byte[] chiave = new byte[Abbinamento.LunghezzaChiave];
        chiave[0] = 0x04;
        chiave[64] = 7;
        byte[] firma = new byte[Abbinamento.LunghezzaFirma];

        Assert.False(Abbinamento.FirmaValida(chiave, ImprontaEsempio, NumeroPcEsempio, NumeroIphoneEsempio, firma));
        Assert.False(Abbinamento.FirmaValida(new byte[10], ImprontaEsempio, NumeroPcEsempio, NumeroIphoneEsempio, firma));
    }

    [Fact]
    public void IlCodiceHaSempreSeiCifre()
    {
        byte[] chiave = new byte[Abbinamento.LunghezzaChiave];
        for (int i = 0; i < 500; i++)
        {
            string codice = Abbinamento.Codice(ImprontaEsempio, chiave, Abbinamento.NuovoNumero(), Abbinamento.NuovoNumero());
            Assert.Matches("^[0-9]{6}$", codice);
        }
    }

    [Fact]
    public void LImpegnoCambiaConIlNumero()
    {
        byte[] chiave = new byte[Abbinamento.LunghezzaChiave];
        Assert.NotEqual(Abbinamento.Impegno(chiave, NumeroPcEsempio), Abbinamento.Impegno(chiave, NumeroIphoneEsempio));
    }

    [Fact]
    public async Task IMessaggiDellAbbinamentoAndataERitorno()
    {
        var presentazione = new Presentazione(new byte[65], Abbinamento.NuovoNumero(), "iPhone");
        presentazione.Chiave[0] = 0x04;
        var sfida = new Sfida(Abbinamento.NuovoNumero());
        var prova = new Prova(Abbinamento.NuovoNumero(), RandomNumberGenerator.GetBytes(64));

        var flusso = new MemoryStream();
        await Protocollo.InviaPresentazioneAsync(flusso, presentazione);
        await Protocollo.InviaSfidaAsync(flusso, sfida);
        await Protocollo.InviaProvaAsync(flusso, prova);
        await Protocollo.InviaEsitoAsync(flusso, new Esito(ValoreEsito.InAttesa));
        flusso.Position = 0;

        var p = Assert.IsType<Presentazione>(await Protocollo.RiceviMessaggioAsync(flusso));
        Assert.Equal(presentazione.Chiave, p.Chiave);
        Assert.Equal(presentazione.Impegno, p.Impegno);
        Assert.Equal("iPhone", p.Nome);
        Assert.Equal(sfida.NumeroPc, Assert.IsType<Sfida>(await Protocollo.RiceviMessaggioAsync(flusso)).NumeroPc);
        var r = Assert.IsType<Prova>(await Protocollo.RiceviMessaggioAsync(flusso));
        Assert.Equal(prova.NumeroIphone, r.NumeroIphone);
        Assert.Equal(prova.Firma, r.Firma);
        Assert.Equal(new Esito(ValoreEsito.InAttesa), await Protocollo.RiceviMessaggioAsync(flusso));
        Assert.Null(await Protocollo.RiceviMessaggioAsync(flusso));
    }

    [Fact]
    public async Task IlFormatoSulFiloEQuelloDocumentato()
    {
        var flusso = new MemoryStream();
        byte[] chiave = Enumerable.Range(0, 65).Select(i => (byte)i).ToArray();
        byte[] impegno = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        await Protocollo.InviaPresentazioneAsync(flusso, new Presentazione(chiave, impegno, "Né"));
        await Protocollo.InviaEsitoAsync(flusso, new Esito(ValoreEsito.Rifiutato));
        byte[] scritto = flusso.ToArray();

        // Lunghezza = tipo + chiave + impegno + "Né" in UTF-8 (3 byte).
        Assert.Equal(new byte[] { 0, 0, 0, 1 + 65 + 32 + 3, 6 }, scritto[..5]);
        Assert.Equal(chiave, scritto[5..70]);
        Assert.Equal(impegno, scritto[70..102]);
        Assert.Equal(new byte[] { (byte)'N', 0xC3, 0xA9 }, scritto[102..105]);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 9, 2 }, scritto[105..]);
    }

    [Fact]
    public async Task MessaggiDiAbbinamentoMalformati()
    {
        // Nome più lungo di 64 byte: non si spedisce e non si accetta.
        var lunga = new Presentazione(new byte[65], new byte[32], new string('x', 65));
        await Assert.ThrowsAsync<ArgumentException>(() => Protocollo.InviaPresentazioneAsync(new MemoryStream(), lunga));
        byte[] presentazioneLunga = [0, 0, 0, 1 + 65 + 32 + 65, 6, .. new byte[65 + 32 + 65]];
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(new MemoryStream(presentazioneLunga)));

        // Chiave corta.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Protocollo.InviaPresentazioneAsync(new MemoryStream(), new Presentazione(new byte[64], new byte[32], "")));

        // Sfida corta, esito sconosciuto.
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(new MemoryStream([0, 0, 0, 2, 7, 1])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocollo.RiceviMessaggioAsync(new MemoryStream([0, 0, 0, 2, 9, 3])));
    }
}
