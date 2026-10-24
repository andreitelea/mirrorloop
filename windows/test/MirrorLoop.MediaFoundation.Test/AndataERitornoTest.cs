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

    [Fact]
    public void IBufferFornitiDaFuoriSiRiempionoComeQuelliNuovi()
    {
        var animazione = new Animazione(Larghezza, Altezza);
        var compressi = new List<FrameCompresso>();
        var inizio = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        using (var compressore = new CompressoreH264(Larghezza, Altezza, Fps, 4_000_000, Fps * 2))
        {
            for (int n = 0; n < 10; n++)
                compressi.AddRange(compressore.Comprimi(animazione.Disegna(n, Fps, inizio.AddSeconds((double)n / Fps)).ToArray()));
        }

        // Due buffer usati a turno, come farebbe una riserva: dentro c'è ancora l'immagine precedente.
        var riserva = new[] { new byte[0], new byte[0] };
        var richieste = new List<int>();
        byte[] Fornisci(int dimensione)
        {
            richieste.Add(dimensione);
            int i = richieste.Count % 2;
            if (riserva[i].Length != dimensione)
                riserva[i] = new byte[dimensione];
            return riserva[i];
        }

        using var normale = new DecompressoreH264();
        using var conRiserva = new DecompressoreH264(Fornisci);
        foreach (FrameCompresso c in compressi)
        {
            FrameDecompresso atteso = normale.Decomprimi(c.DatiH264, c.Numero).Single();
            FrameDecompresso ottenuto = conRiserva.Decomprimi(c.DatiH264, c.Numero).Single();
            Assert.Same(riserva[richieste.Count % 2], ottenuto.Nv12);
            Assert.Equal(atteso.Nv12, ottenuto.Nv12);
        }
        Assert.NotEmpty(compressi);
        Assert.Equal(compressi.Count, richieste.Count);
        Assert.All(richieste, d => Assert.Equal(richieste[0], d));
    }

    /// <summary>
    /// Misure non multiple di 16, come lo schermo di un iPhone 17 Pro Max: il decompressore lavora su
    /// 1328×2880 ma deve restituire esattamente 1320×2868, senza bordo, con luminosità e colori al loro posto.
    /// </summary>
    [Fact]
    public void ConMisureNonMultipleDi16EsceSoloLaZonaVera()
    {
        const int larghezza = 1320, altezza = 2868;
        var animazione = new Animazione(larghezza, altezza);
        var originali = new List<byte[]>();
        var compressi = new List<FrameCompresso>();
        var inizio = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        using (var compressore = new CompressoreH264(larghezza, altezza, Fps, 10_000_000, Fps * 2))
        {
            for (int n = 0; n < 5; n++)
            {
                byte[] frame = animazione.Disegna(n, Fps, inizio.AddSeconds((double)n / Fps)).ToArray();
                originali.Add(frame);
                compressi.AddRange(compressore.Comprimi(frame));
            }
        }

        using var decompressore = new DecompressoreH264();
        for (int n = 0; n < compressi.Count; n++)
        {
            FrameDecompresso d = decompressore.Decomprimi(compressi[n].DatiH264, compressi[n].Numero).Single();
            Assert.Equal(larghezza, d.Larghezza);
            Assert.Equal(altezza, d.Altezza);
            Assert.Equal(d.Passo * altezza * 3 / 2, d.Nv12.Length);

            // Se il piano dei colori fosse preso dal punto sbagliato (dentro il bordo), i colori sarebbero sbagliati.
            var (luminosita, colori) = DifferenzaMedia(originali[n], larghezza, altezza, d);
            Assert.True(luminosita < 3.0, $"Frame {n}: differenza media di luminosità {luminosita:F2} (massimo 3).");
            Assert.True(colori < 3.0, $"Frame {n}: differenza media dei colori {colori:F2} (massimo 3).");
        }
        Assert.Equal(5, compressi.Count);
    }

    /// <summary>Differenza media (valori 0–255) tra originale NV12 compatto e decompresso, per luminosità e colori.</summary>
    private static (double Luminosita, double Colori) DifferenzaMedia(byte[] originale, int larghezza, int altezza, FrameDecompresso d)
    {
        long sommaY = 0, sommaUv = 0;
        for (int riga = 0; riga < altezza; riga++)
        {
            for (int x = 0; x < larghezza; x++)
                sommaY += Math.Abs(originale[riga * larghezza + x] - d.Nv12[riga * d.Passo + x]);
        }
        int inizioOriginale = larghezza * altezza, inizioDecompresso = d.Passo * altezza;
        for (int riga = 0; riga < altezza / 2; riga++)
        {
            for (int x = 0; x < larghezza; x++)
                sommaUv += Math.Abs(originale[inizioOriginale + riga * larghezza + x] - d.Nv12[inizioDecompresso + riga * d.Passo + x]);
        }
        return ((double)sommaY / (larghezza * altezza), (double)sommaUv / (larghezza * altezza / 2));
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
