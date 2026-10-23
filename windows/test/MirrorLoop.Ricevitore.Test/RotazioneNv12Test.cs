using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore.Test;

public class RotazioneNv12Test
{
    // Un'immagine 4×2: luminosità numerate da 1 a 8, e i due punti di colore (U, V) = (101, 102) e (103, 104).
    //   Y:  1 2 3 4      UV: (101,102) (103,104)
    //       5 6 7 8
    private static readonly byte[] Immagine = [1, 2, 3, 4, 5, 6, 7, 8, 101, 102, 103, 104];

    [Fact]
    public void Novanta()
    {
        // Girata in senso orario: 2×4, la colonna di sinistra (1, 5) diventa la riga in alto letta da destra.
        //   Y:  5 1     UV: (101,102)
        //       6 2         (103,104)
        //       7 3
        //       8 4
        Assert.Equal((2, 4), Ruota(RotazioneVideo.Gradi90, out byte[] girata));
        Assert.Equal(new byte[] { 5, 1, 6, 2, 7, 3, 8, 4, 101, 102, 103, 104 }, girata);
    }

    [Fact]
    public void Centottanta()
    {
        Assert.Equal((4, 2), Ruota(RotazioneVideo.Gradi180, out byte[] girata));
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1, 103, 104, 101, 102 }, girata);
    }

    [Fact]
    public void DuecentoSettanta()
    {
        //   Y:  4 8     UV: (103,104)
        //       3 7         (101,102)
        //       2 6
        //       1 5
        Assert.Equal((2, 4), Ruota(RotazioneVideo.Gradi270, out byte[] girata));
        Assert.Equal(new byte[] { 4, 8, 3, 7, 2, 6, 1, 5, 103, 104, 101, 102 }, girata);
    }

    [Fact]
    public void SenzaRotazioneCompattaSoloLeRighe()
    {
        // Passo 6: due byte di riempimento in fondo a ogni riga, che non devono comparire.
        byte[] conPasso = [1, 2, 3, 4, 0, 0, 5, 6, 7, 8, 0, 0, 101, 102, 103, 104, 0, 0];
        var girata = new byte[12];
        Assert.Equal((4, 2), RotazioneNv12.Ruota(conPasso, 4, 2, 6, RotazioneVideo.Nessuna, girata));
        Assert.Equal(Immagine, girata);
    }

    [Fact]
    public void QuattroGiriDiNovantaRiportanoAlPuntoDiPartenza()
    {
        var casuale = new Random(7);
        int larghezza = 16, altezza = 10;
        var originale = new byte[larghezza * altezza * 3 / 2];
        casuale.NextBytes(originale);

        byte[] corrente = originale;
        (int l, int a) = (larghezza, altezza);
        for (int giro = 0; giro < 4; giro++)
        {
            var girata = new byte[corrente.Length];
            (l, a) = RotazioneNv12.Ruota(corrente, l, a, l, RotazioneVideo.Gradi90, girata);
            corrente = girata;
        }
        Assert.Equal((larghezza, altezza), (l, a));
        Assert.Equal(originale, corrente);
    }

    [Fact]
    public void LeMisureNonValideSonoRifiutate()
    {
        var destinazione = new byte[100];
        Assert.Throws<ArgumentException>(() => RotazioneNv12.Ruota(new byte[100], 3, 2, 4, RotazioneVideo.Gradi90, destinazione));
        Assert.Throws<ArgumentException>(() => RotazioneNv12.Ruota(new byte[100], 4, 2, 2, RotazioneVideo.Gradi90, destinazione));
        Assert.Throws<ArgumentException>(() => RotazioneNv12.Ruota(new byte[100], 4, 2, 4, RotazioneVideo.Gradi90, new byte[11]));
    }

    private static (int, int) Ruota(RotazioneVideo rotazione, out byte[] girata)
    {
        girata = new byte[Immagine.Length];
        return RotazioneNv12.Ruota(Immagine, 4, 2, 4, rotazione, girata);
    }
}
