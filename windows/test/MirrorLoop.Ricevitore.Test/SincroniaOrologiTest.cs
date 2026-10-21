using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore.Test;

public class SincroniaOrologiTest
{
    // L'orologio dell'iPhone è avanti di 1 secondo esatto rispetto a quello del PC.
    private const long Scarto = 1_000_000;

    /// <summary>Un giro ping-pong con andata e ritorno di durata data (µs) e 50 µs di attesa sull'iPhone.</summary>
    private static (Pong Pong, long T4) Giro(long t1, long andata, long ritorno)
    {
        long t2 = t1 + andata + Scarto;
        long t3 = t2 + 50;
        long t4 = t3 - Scarto + ritorno;
        return (new Pong(1, t1, t2, t3), t4);
    }

    [Fact]
    public void SenzaMisureNonESincronizzato()
    {
        var sincronia = new SincroniaOrologi();

        Assert.Null(sincronia.Migliore);
        Assert.Null(sincronia.InOraDelPc(123));
    }

    [Fact]
    public void ConViaggiSimmetriciLoScartoEEsatto()
    {
        var sincronia = new SincroniaOrologi();
        var (pong, t4) = Giro(10_000_000, andata: 4_000, ritorno: 4_000);

        sincronia.Registra(pong, t4);

        Assert.Equal(Scarto, sincronia.Migliore!.Value.ScartoMicrosecondi);
        Assert.Equal(8_000, sincronia.Migliore!.Value.AndataRitornoMicrosecondi);
        Assert.Equal(5_000_000, sincronia.InOraDelPc(6_000_000));
    }

    [Fact]
    public void SiTieneLaMisuraConIlViaggioPiuBreve()
    {
        // Il secondo ritorno è rimasto 60 ms in coda dietro al video: errore di 30 ms sullo scarto.
        var sincronia = new SincroniaOrologi();
        var (buono, t4Buono) = Giro(10_000_000, andata: 3_000, ritorno: 3_000);
        var (lento, t4Lento) = Giro(11_000_000, andata: 3_000, ritorno: 63_000);

        sincronia.Registra(lento, t4Lento);
        sincronia.Registra(buono, t4Buono);

        Assert.Equal(Scarto, sincronia.Migliore!.Value.ScartoMicrosecondi);
        Assert.Equal(6_000, sincronia.Migliore!.Value.AndataRitornoMicrosecondi);
    }

    [Fact]
    public void LeMisureTroppoVecchieSiDimenticano()
    {
        var sincronia = new SincroniaOrologi();
        var (ottima, t4Ottima) = Giro(1_000_000, andata: 500, ritorno: 500);
        sincronia.Registra(ottima, t4Ottima);
        for (int i = 0; i < SincroniaOrologi.MisureRecenti; i++)
        {
            var (pong, t4) = Giro(2_000_000 + i * 1_000_000, andata: 5_000, ritorno: 5_000);
            sincronia.Registra(pong, t4);
        }

        Assert.Equal(10_000, sincronia.Migliore!.Value.AndataRitornoMicrosecondi);
    }

    [Fact]
    public void UnPongIncoerenteSiIgnora()
    {
        // T4 prima di T1: impossibile, si scarta.
        var sincronia = new SincroniaOrologi();
        sincronia.Registra(new Pong(1, 10_000_000, 11_000_000, 11_000_050), 9_000_000);

        Assert.Null(sincronia.Migliore);
    }

    [Fact]
    public void RicominciaDimenticaTutto()
    {
        var sincronia = new SincroniaOrologi();
        var (pong, t4) = Giro(10_000_000, andata: 4_000, ritorno: 4_000);
        sincronia.Registra(pong, t4);

        sincronia.Ricomincia();

        Assert.Null(sincronia.Migliore);
    }
}
