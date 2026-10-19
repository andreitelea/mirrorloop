namespace MirrorLoop.MediaFoundation.Test;

public class AnnexBTest
{
    // Un frame come lo produce il compressore Microsoft: AUD, SPS, PPS, IDR (contenuti abbreviati).
    private static readonly byte[] Frame =
    [
        0, 0, 0, 1, 0x09, 0x10,             // AUD (tipo 9)
        0, 0, 0, 1, 0x67, 0x42, 0xC0,       // SPS (tipo 7)
        0, 0, 0, 1, 0x68, 0xCE,             // PPS (tipo 8)
        0, 0, 1, 0x65, 0x88, 0x84,          // IDR (tipo 5), con codice di inizio corto
    ];

    [Fact]
    public void TrovaCodiciDiInizioLunghiECorti()
    {
        Assert.Equal(0, AnnexB.TrovaCodiceInizio(Frame, 0));
        // Si cerca sempre partendo da dopo un codice di inizio: dentro "00 00 00 01" c'è anche un "00 00 01".
        Assert.Equal(6, AnnexB.TrovaCodiceInizio(Frame, 4));
        // Dalla posizione 17 (dopo il codice di inizio del PPS) il prossimo è quello corto dell'IDR.
        Assert.Equal(19, AnnexB.TrovaCodiceInizio(Frame, 17));
        Assert.Equal(-1, AnnexB.TrovaCodiceInizio(Frame, 20));
    }

    [Fact]
    public void RiconosceITipiDiNal()
    {
        Assert.Equal(new HashSet<int> { 9, 7, 8, 5 }, AnnexB.TipiNal(Frame));
    }

    [Fact]
    public void TogliereGliAudNonToccaGliAltriNal()
    {
        byte[] senzaAud = AnnexB.TogliNal(Frame, AnnexB.NalAud);

        Assert.Equal(Frame[6..], senzaAud);
        Assert.Equal(new HashSet<int> { 7, 8, 5 }, AnnexB.TipiNal(senzaAud));
    }

    [Fact]
    public void SenzaNalDelTipoIndicatoIDatiRestanoUguali()
    {
        Assert.Equal(Frame, AnnexB.TogliNal(Frame, AnnexB.NalSei));
    }

    [Fact]
    public void DatiSenzaCodiciDiInizio()
    {
        byte[] dati = [1, 2, 3, 4];

        Assert.Empty(AnnexB.TipiNal(dati));
        Assert.Empty(AnnexB.TogliNal(dati, AnnexB.NalAud));
    }
}
