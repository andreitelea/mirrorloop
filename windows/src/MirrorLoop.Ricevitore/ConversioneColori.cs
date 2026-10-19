namespace MirrorLoop.Ricevitore;

/// <summary>
/// Da NV12 (il formato del decompressore) a BGRA (il formato che la finestra sa mostrare).
///
/// Formule BT.709 "a gamma limitata", le inverse di quelle usate dal mittente:
///   R = 1,164·(Y−16)               + 1,793·(V−128)
///   G = 1,164·(Y−16) − 0,213·(U−128) − 0,533·(V−128)
///   B = 1,164·(Y−16) + 2,112·(U−128)
/// I coefficienti sono moltiplicati per 256 e arrotondati, per lavorare con numeri interi (più veloci).
/// </summary>
public static class ConversioneColori
{
    private const int CoeffY = 298;   // 1,164 × 256
    private const int CoeffRv = 459;  // 1,793 × 256
    private const int CoeffGu = 55;   // 0,213 × 256
    private const int CoeffGv = 136;  // 0,533 × 256
    private const int CoeffBu = 541;  // 2,112 × 256

    /// <param name="nv12">Piano Y seguito dal piano UV, entrambi con righe lunghe <paramref name="passoNv12"/> byte.</param>
    /// <param name="bgra">Destinazione: 4 byte per pixel (blu, verde, rosso, opacità), righe lunghe <paramref name="passoBgra"/> byte.</param>
    public static void Nv12ABgra(ReadOnlySpan<byte> nv12, int larghezza, int altezza, int passoNv12, Span<byte> bgra, int passoBgra)
    {
        if (larghezza % 2 != 0 || altezza % 2 != 0)
            throw new ArgumentException("Larghezza e altezza devono essere pari.");
        if (nv12.Length < passoNv12 * altezza * 3 / 2)
            throw new ArgumentException("Dati NV12 troppo corti.", nameof(nv12));
        if (bgra.Length < passoBgra * altezza)
            throw new ArgumentException("Destinazione troppo corta.", nameof(bgra));

        int inizioUv = passoNv12 * altezza;
        for (int riga = 0; riga < altezza; riga++)
        {
            ReadOnlySpan<byte> y = nv12.Slice(riga * passoNv12, larghezza);
            // Una riga di colore ogni due righe di luminosità.
            ReadOnlySpan<byte> uv = nv12.Slice(inizioUv + riga / 2 * passoNv12, larghezza);
            Span<byte> destinazione = bgra.Slice(riga * passoBgra, larghezza * 4);

            for (int x = 0; x < larghezza; x += 2)
            {
                int u = uv[x] - 128;
                int v = uv[x + 1] - 128;
                // La parte di colore è uguale per i due pixel affiancati: la calcoliamo una volta.
                int rossoCol = CoeffRv * v + 128;
                int verdeCol = -CoeffGu * u - CoeffGv * v + 128;
                int bluCol = CoeffBu * u + 128;

                Scrivi(destinazione.Slice(x * 4, 4), CoeffY * (y[x] - 16), rossoCol, verdeCol, bluCol);
                Scrivi(destinazione.Slice(x * 4 + 4, 4), CoeffY * (y[x + 1] - 16), rossoCol, verdeCol, bluCol);
            }
        }
    }

    private static void Scrivi(Span<byte> pixel, int luce, int rosso, int verde, int blu)
    {
        pixel[0] = Limita((luce + blu) >> 8);
        pixel[1] = Limita((luce + verde) >> 8);
        pixel[2] = Limita((luce + rosso) >> 8);
        pixel[3] = 255;
    }

    private static byte Limita(int valore) => (byte)Math.Clamp(valore, 0, 255);
}
