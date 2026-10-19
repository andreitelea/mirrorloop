namespace MirrorLoop.Ricevitore.Test;

public class ConversioneColoriTest
{
    /// <summary>Converte un'immagine 2×2 di un solo colore (Y, U, V) e restituisce il primo pixel come (R, G, B).</summary>
    private static (int R, int G, int B) Converti(byte y, byte u, byte v)
    {
        byte[] nv12 = [y, y, y, y, u, v];
        byte[] bgra = new byte[2 * 2 * 4];

        ConversioneColori.Nv12ABgra(nv12, 2, 2, 2, bgra, 8);

        // Tutti e quattro i pixel devono essere uguali e opachi.
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(bgra[0..4], bgra[(i * 4)..(i * 4 + 4)]);
            Assert.Equal(255, bgra[i * 4 + 3]);
        }
        return (bgra[2], bgra[1], bgra[0]);
    }

    // I valori YUV sono quelli che il mittente produce per i suoi campioni di colore (BT.709, gamma limitata).
    [Theory]
    [InlineData(235, 128, 128, 255, 255, 255)] // bianco
    [InlineData(16, 128, 128, 0, 0, 0)]        // nero
    [InlineData(63, 102, 240, 255, 0, 0)]      // rosso
    [InlineData(173, 42, 26, 0, 255, 0)]       // verde
    [InlineData(32, 240, 118, 0, 0, 255)]      // blu
    [InlineData(126, 128, 128, 128, 128, 128)] // grigio medio
    public void ColoriNoti(byte y, byte u, byte v, int r, int g, int b)
    {
        var (rr, gg, bb) = Converti(y, u, v);

        // L'arrotondamento ai numeri interi, in andata e in ritorno, sposta i valori di poco.
        const int Tolleranza = 3;
        Assert.InRange(rr, r - Tolleranza, r + Tolleranza);
        Assert.InRange(gg, g - Tolleranza, g + Tolleranza);
        Assert.InRange(bb, b - Tolleranza, b + Tolleranza);
    }

    [Fact]
    public void ValoriFuoriGammaVengonoLimitati()
    {
        // Y=255 è "più bianco del bianco" nella gamma limitata: deve restare 255, non tornare a zero.
        var (r, g, b) = Converti(255, 128, 128);
        Assert.Equal((255, 255, 255), (r, g, b));

        var (r0, g0, b0) = Converti(0, 128, 128);
        Assert.Equal((0, 0, 0), (r0, g0, b0));
    }

    [Fact]
    public void RispettaIlPassoDelleRighe()
    {
        // Immagine 2×2 con righe lunghe 4 byte (2 di immagine + 2 di riempimento).
        // Riga 0 bianca, riga 1 nera; il riempimento contiene valori assurdi che non devono comparire.
        byte[] nv12 =
        [
            235, 235, 99, 99,
            16, 16, 99, 99,
            128, 128, 99, 99,
        ];
        byte[] bgra = new byte[2 * 12];

        ConversioneColori.Nv12ABgra(nv12, 2, 2, 4, bgra, 12);

        Assert.Equal(255, bgra[2]);  // riga 0, pixel 0: rosso pieno (bianco)
        Assert.Equal(0, bgra[12 + 2]); // riga 1, pixel 0: rosso nullo (nero)
        Assert.Equal(0, bgra[8]);   // il riempimento della destinazione non viene toccato
    }
}
