namespace MittenteTest;

/// <summary>
/// Disegna i frame di prova direttamente in NV12, il formato che il compressore H.264 vuole:
/// prima il piano della luminosità (Y, un byte per pixel), poi il piano del colore (U e V
/// alternati, un valore ogni blocco di 2×2 pixel). Così non serve nessuna conversione.
/// </summary>
public sealed class Animazione
{
    public int Larghezza { get; }
    public int Altezza { get; }

    /// <summary>Byte di un frame NV12: luminosità piena + colore a un quarto.</summary>
    public int DimensioneFrame => Larghezza * Altezza * 3 / 2;

    private readonly byte[] _frame;

    public Animazione(int larghezza, int altezza)
    {
        // NV12 lavora a blocchi di 2×2 pixel per il colore.
        if (larghezza % 2 != 0 || altezza % 2 != 0)
            throw new ArgumentException("Larghezza e altezza devono essere pari.");

        Larghezza = larghezza;
        Altezza = altezza;
        _frame = new byte[DimensioneFrame];
    }

    /// <summary>
    /// Disegna il frame numero <paramref name="numero"/>. Il buffer restituito viene riusato
    /// al frame successivo: va copiato o consumato subito.
    /// </summary>
    public ReadOnlySpan<byte> Disegna(long numero, double fps, DateTime istante)
    {
        // Sfondo che cambia tinta in 20 secondi: colori sbagliati in uscita si notano subito.
        double tinta = numero / fps / 20.0 % 1.0;
        Riempi(0, 0, Larghezza, Altezza, DaTinta(tinta, 0.35));

        // Barra che attraversa lo schermo in 2 secondi: se "scatta", il ritmo non è regolare.
        int larghezzaBarra = Larghezza / 32 & ~1;
        double posizione = numero / fps / 2.0 % 1.0;
        int xBarra = (int)(posizione * (Larghezza - larghezzaBarra)) & ~1;
        Riempi(xBarra, 0, larghezzaBarra, Altezza, Bianco);

        // Campioni di colore fissi in basso: rosso, verde, blu, bianco, nero.
        int lato = Altezza / 8 & ~1;
        Colore[] campioni = [Rosso, Verde, Blu, Bianco, Nero];
        for (int i = 0; i < campioni.Length; i++)
            Riempi(lato / 2 + i * (lato + lato / 4) & ~1, Altezza - lato - lato / 2 & ~1, lato, lato, campioni[i]);

        // Numero del frame e orologio, in cifre da display a 7 segmenti.
        int altezzaCifra = Altezza / 6 & ~1;
        int margine = Altezza / 20 & ~1;
        ScriviTesto($"{numero % 1_000_000:D6}", margine, margine, altezzaCifra);
        ScriviTesto(istante.ToLocalTime().ToString("HH:mm:ss.fff"), margine, margine * 2 + altezzaCifra, altezzaCifra);

        return _frame;
    }

    // --- Colori ---

    private readonly record struct Colore(byte Y, byte U, byte V);

    private static readonly Colore Bianco = DaRgb(255, 255, 255);
    private static readonly Colore Nero = DaRgb(0, 0, 0);
    private static readonly Colore Rosso = DaRgb(255, 0, 0);
    private static readonly Colore Verde = DaRgb(0, 255, 0);
    private static readonly Colore Blu = DaRgb(0, 0, 255);

    /// <summary>
    /// Da RGB a YUV con i coefficienti BT.709 "a gamma limitata" (Y da 16 a 235),
    /// lo standard per il video HD.
    /// </summary>
    private static Colore DaRgb(byte r, byte g, byte b) => new(
        (byte)Math.Round(16 + 0.1826 * r + 0.6142 * g + 0.0620 * b),
        (byte)Math.Round(128 - 0.1006 * r - 0.3386 * g + 0.4392 * b),
        (byte)Math.Round(128 + 0.4392 * r - 0.3989 * g - 0.0403 * b));

    private static Colore DaTinta(double tinta, double luminosita)
    {
        // Ruota di colori semplificata: tre sinusoidi sfasate di un terzo di giro.
        static byte Canale(double t, double l) => (byte)(255 * l * (0.5 + 0.5 * Math.Cos(2 * Math.PI * t)));
        return DaRgb(
            Canale(tinta, luminosita),
            Canale(tinta - 1.0 / 3, luminosita),
            Canale(tinta - 2.0 / 3, luminosita));
    }

    // --- Disegno ---

    /// <summary>Riempie un rettangolo. Coordinate e dimensioni pari, per allinearsi ai blocchi 2×2 del colore.</summary>
    private void Riempi(int x, int y, int larghezza, int altezza, Colore c)
    {
        int x0 = Math.Clamp(x, 0, Larghezza), x1 = Math.Clamp(x + larghezza, 0, Larghezza);
        int y0 = Math.Clamp(y, 0, Altezza), y1 = Math.Clamp(y + altezza, 0, Altezza);
        if (x0 >= x1 || y0 >= y1)
            return;

        Span<byte> piano = _frame;
        for (int riga = y0; riga < y1; riga++)
            piano.Slice(riga * Larghezza + x0, x1 - x0).Fill(c.Y);

        int inizioColore = Larghezza * Altezza;
        for (int riga = y0 / 2; riga < y1 / 2; riga++)
        {
            Span<byte> uv = piano.Slice(inizioColore + riga * Larghezza + x0, x1 - x0);
            for (int i = 0; i + 1 < uv.Length; i += 2)
            {
                uv[i] = c.U;
                uv[i + 1] = c.V;
            }
        }
    }

    //  Segmenti:   _a_
    //            f|   |b
    //              _g_
    //            e|   |c
    //              _d_
    private static readonly Dictionary<char, string> Segmenti = new()
    {
        ['0'] = "abcdef", ['1'] = "bc", ['2'] = "abged", ['3'] = "abgcd", ['4'] = "fgbc",
        ['5'] = "afgcd", ['6'] = "afgedc", ['7'] = "abc", ['8'] = "abcdefg", ['9'] = "abcdfg",
    };

    private void ScriviTesto(string testo, int x, int y, int altezzaCifra)
    {
        int larghezzaCifra = altezzaCifra / 2 & ~1;
        int spessore = Math.Max(2, altezzaCifra / 10 & ~1);
        int spazio = larghezzaCifra / 3 & ~1;

        // Riquadro scuro dietro al testo, per leggerlo su qualunque sfondo.
        int larghezzaTotale = testo.Sum(ch => char.IsDigit(ch) ? larghezzaCifra + spazio : spessore * 2 + spazio);
        Riempi(x - spazio, y - spazio, larghezzaTotale + spazio, altezzaCifra + spazio * 2, Nero);

        foreach (char ch in testo)
        {
            if (Segmenti.TryGetValue(ch, out string? accesi))
            {
                DisegnaCifra(accesi, x, y, larghezzaCifra, altezzaCifra, spessore);
                x += larghezzaCifra + spazio;
            }
            else
            {
                // ':' e '.' diventano piccoli quadrati.
                if (ch == ':')
                    Riempi(x, y + altezzaCifra / 3, spessore, spessore, Bianco);
                Riempi(x, y + altezzaCifra * 2 / 3 & ~1, spessore, spessore, Bianco);
                x += spessore * 2 + spazio;
            }
        }
    }

    private void DisegnaCifra(string accesi, int x, int y, int l, int h, int s)
    {
        int meta = y + (h - s) / 2 & ~1;
        foreach (char seg in accesi)
        {
            switch (seg)
            {
                case 'a': Riempi(x, y, l, s, Bianco); break;
                case 'g': Riempi(x, meta, l, s, Bianco); break;
                case 'd': Riempi(x, y + h - s, l, s, Bianco); break;
                case 'f': Riempi(x, y, s, h / 2, Bianco); break;
                case 'b': Riempi(x + l - s, y, s, h / 2, Bianco); break;
                case 'e': Riempi(x, meta, s, h - (meta - y), Bianco); break;
                case 'c': Riempi(x + l - s, meta, s, h - (meta - y), Bianco); break;
            }
        }
    }
}
