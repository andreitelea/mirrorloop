using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Gira un'immagine NV12 di 90°, 180° o 270° in senso orario, per consegnarla già dritta a chi
/// non sa ruotarla da solo (la sorgente di OBS). La finestra invece la gira con la scheda video.
/// </summary>
public static class RotazioneNv12
{
    /// <summary>Le misure dell'immagine dopo la rotazione.</summary>
    public static (int Larghezza, int Altezza) Misure(int larghezza, int altezza, RotazioneVideo rotazione) =>
        rotazione is RotazioneVideo.Gradi90 or RotazioneVideo.Gradi270 ? (altezza, larghezza) : (larghezza, altezza);

    /// <param name="nv12">Piano Y (altezza righe) seguito dal piano UV (altezza / 2 righe), righe lunghe <paramref name="passo"/> byte.</param>
    /// <param name="destinazione">Riceve l'immagine girata con le righe compatte (passo = nuova larghezza).</param>
    /// <returns>Le misure dell'immagine girata.</returns>
    public static (int Larghezza, int Altezza) Ruota(ReadOnlySpan<byte> nv12, int larghezza, int altezza, int passo,
        RotazioneVideo rotazione, Span<byte> destinazione)
    {
        if (larghezza <= 0 || altezza <= 0 || larghezza % 2 != 0 || altezza % 2 != 0)
            throw new ArgumentException($"Misure NV12 non valide: {larghezza}×{altezza} (servono positive e pari).");
        if (passo < larghezza)
            throw new ArgumentException($"Passo {passo} minore della larghezza {larghezza}.", nameof(passo));
        if (nv12.Length < passo * altezza * 3 / 2)
            throw new ArgumentException("Dati NV12 troppo corti.", nameof(nv12));
        int byteY = larghezza * altezza;
        if (destinazione.Length < byteY * 3 / 2)
            throw new ArgumentException("Destinazione troppo corta.", nameof(destinazione));

        // Il piano UV è un'immagine a metà risoluzione con 2 byte per punto (U e V): si gira allo
        // stesso modo, tenendo insieme le coppie.
        RuotaPiano(nv12, larghezza, altezza, passo, 1, rotazione, destinazione[..byteY]);
        RuotaPiano(nv12[(passo * altezza)..], larghezza / 2, altezza / 2, passo, 2, rotazione,
            destinazione.Slice(byteY, byteY / 2));
        return Misure(larghezza, altezza, rotazione);
    }

    private static void RuotaPiano(ReadOnlySpan<byte> sorgente, int larghezza, int altezza, int passo, int bytePerPunto,
        RotazioneVideo rotazione, Span<byte> destinazione)
    {
        int larghezzaGirata = Misure(larghezza, altezza, rotazione).Larghezza;
        for (int y = 0; y < altezza; y++)
        {
            ReadOnlySpan<byte> riga = sorgente.Slice(y * passo, larghezza * bytePerPunto);
            for (int x = 0; x < larghezza; x++)
            {
                (int xg, int yg) = rotazione switch
                {
                    RotazioneVideo.Gradi90 => (altezza - 1 - y, x),
                    RotazioneVideo.Gradi180 => (larghezza - 1 - x, altezza - 1 - y),
                    RotazioneVideo.Gradi270 => (y, larghezza - 1 - x),
                    _ => (x, y),
                };
                int da = x * bytePerPunto;
                int a = (yg * larghezzaGirata + xg) * bytePerPunto;
                destinazione[a] = riga[da];
                if (bytePerPunto == 2)
                    destinazione[a + 1] = riga[da + 1];
            }
        }
    }
}
