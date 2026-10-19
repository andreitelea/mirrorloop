using MittenteTest;

namespace MirrorLoop.MediaFoundation.Test;

/// <summary>
/// Il test più importante della catena video: l'animazione di prova viene compressa con il nostro
/// compressore e decompressa con il nostro decompressore, e le immagini devono tornare (quasi) uguali.
/// "Quasi" perché la compressione H.264 perde un po' di dettaglio per definizione.
/// </summary>
public class AndataERitornoTest
{
    private const int Larghezza = 1280;
    private const int Altezza = 720;
    private const int Fps = 30;
    private const int NumeroFrame = 90;

    [Fact]
    public void LAnimazioneCompressaEDecompressaTornaUguale()
    {
        var animazione = new Animazione(Larghezza, Altezza);
        var originali = new List<byte[]>();
        var compressi = new List<FrameCompresso>();
        var inizio = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        using (var compressore = new CompressoreH264(Larghezza, Altezza, Fps, 4_000_000, Fps * 2))
        {
            for (int n = 0; n < NumeroFrame; n++)
            {
                byte[] frame = animazione.Disegna(n, Fps, inizio.AddSeconds((double)n / Fps)).ToArray();
                originali.Add(frame);
                compressi.AddRange(compressore.Comprimi(frame));
            }
            compressi.AddRange(compressore.Concludi());
        }

        var decompressi = new List<FrameDecompresso>();
        using (var decompressore = new DecompressoreH264())
        {
            foreach (FrameCompresso c in compressi)
            {
                // Bassa latenza vuol dire: un frame entra, un frame esce subito, niente tenuto da parte.
                var usciti = decompressore.Decomprimi(c.DatiH264, tempo: c.Numero);
                Assert.True(usciti.Count == 1, $"Frame {c.Numero}: usciti {usciti.Count} frame invece di 1.");
                decompressi.AddRange(usciti);
            }
            Assert.Empty(decompressore.Concludi());
        }

        Assert.Equal(NumeroFrame, compressi.Count);
        Assert.Equal(NumeroFrame, decompressi.Count);

        for (int n = 0; n < NumeroFrame; n++)
        {
            FrameDecompresso d = decompressi[n];
            Assert.Equal(Larghezza, d.Larghezza);
            Assert.Equal(Altezza, d.Altezza);
            // Il "tempo" passato al decompressore torna uguale: è ciò che permette di misurare la latenza.
            Assert.Equal(n, d.Tempo);

            double differenza = DifferenzaMediaLuminosita(originali[n], d);
            Assert.True(differenza < 3.0, $"Frame {n}: differenza media di luminosità {differenza:F2} (massimo 3).");
        }
    }

    /// <summary>Di quanto differisce in media la luminosità (piano Y, valori 0–255) tra originale e decompresso.</summary>
    private static double DifferenzaMediaLuminosita(byte[] originale, FrameDecompresso d)
    {
        long somma = 0;
        for (int riga = 0; riga < Altezza; riga++)
        {
            for (int x = 0; x < Larghezza; x++)
                somma += Math.Abs(originale[riga * Larghezza + x] - d.Nv12[riga * d.Passo + x]);
        }
        return (double)somma / (Larghezza * Altezza);
    }
}
