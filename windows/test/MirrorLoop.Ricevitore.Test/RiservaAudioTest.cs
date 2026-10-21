using System.Buffers.Binary;

namespace MirrorLoop.Ricevitore.Test;

public class RiservaAudioTest
{
    // Numeri piccoli per leggere i test a mente: 1000 byte al secondo, stereo (blocchi da 4 byte),
    // obiettivo fisso 40 ms = 40 byte, tolleranza 20 ms, massimo 100 ms = 100 byte. Senza
    // dissolvenze, così i byte in uscita sono esattamente quelli in entrata.
    private static RiservaAudio Crea() =>
        new(1000, 2, Ms(40), Ms(40), Ms(20), Ms(100), TimeSpan.Zero);

    // Valori reali: 48 kHz stereo a 16 bit, come dall'iPhone.
    private const int ByteAlSecondoReali = 48_000 * 4;

    private static RiservaAudio CreaReale() =>
        new(ByteAlSecondoReali, 2, Ms(60), Ms(60), Ms(20), Ms(400), TimeSpan.Zero);

    // Le regole vere del ricevitore: obiettivo da 60 a 100 ms, taglio oltre 140, dissolvenze da 5 ms.
    private static RiservaAudio CreaAdattiva(TimeProvider tempo) =>
        new(ByteAlSecondoReali, 2, Ms(60), Ms(100), Ms(20), Ms(140), Ms(5), tempo);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static byte[] Millisecondi(int ms) => Byte(ByteAlSecondoReali / 1000 * ms, 9);

    /// <summary>Stereo, tutti i campioni uguali a <paramref name="valore"/>.</summary>
    private static byte[] Costante(int ms, short valore)
    {
        int istanti = 48 * ms;
        var dati = new byte[istanti * 4];
        for (int i = 0; i < istanti * 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(dati.AsSpan(i * 2), valore);
        return dati;
    }

    /// <summary>Il canale sinistro di ogni istante, per guardare la forma d'onda in uscita.</summary>
    private static short[] Sinistro(byte[] uscita) =>
        Enumerable.Range(0, uscita.Length / 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(uscita.AsSpan(i * 4))).ToArray();

    /// <summary>Un orologio fermo che avanza solo quando lo dice il test (1 tic = 1 ms).</summary>
    private sealed class TempoFinto : TimeProvider
    {
        public long Adesso { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Adesso;
    }

    /// <summary>Campioni stereo con valori crescenti (0, 1, 2…), uguali sui due canali.</summary>
    private static byte[] Rampa(int istanti)
    {
        var dati = new byte[istanti * 4];
        for (int i = 0; i < istanti; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(dati.AsSpan(i * 4), (short)i);
            BinaryPrimitives.WriteInt16LittleEndian(dati.AsSpan(i * 4 + 2), (short)i);
        }
        return dati;
    }

    private static byte[] Byte(int quanti, byte valore) => Enumerable.Repeat(valore, quanti).ToArray();

    private static byte[] Leggi(RiservaAudio riserva, int quanti)
    {
        var uscita = new byte[quanti];
        Assert.Equal(quanti, riserva.Leggi(uscita));
        return uscita;
    }

    [Fact]
    public void FinoAllObiettivoSuonaSilenzio()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(36, 7));

        Assert.All(Leggi(riserva, 8), b => Assert.Equal(0, b));
        Assert.Equal(36, riserva.Riserva.TotalMilliseconds, 3);
    }

    [Fact]
    public void RaggiuntoLObiettivoSuonaICampioniInOrdine()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(20, 1));
        riserva.Aggiungi(Byte(20, 2));

        Assert.Equal([.. Byte(20, 1), .. Byte(4, 2)], Leggi(riserva, 24));
        Assert.Equal(Byte(16, 2), Leggi(riserva, 16));
        Assert.Equal(0, riserva.Buchi);
    }

    [Fact]
    public void QuandoFinisconoICampioniCompletaConSilenzioEContaUnBuco()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(40, 5));

        Assert.Equal([.. Byte(40, 5), .. Byte(8, 0)], Leggi(riserva, 48));
        Assert.Equal(1, riserva.Buchi);
    }

    [Fact]
    public void DopoUnBucoAspettaDiNuovoLObiettivo()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(40, 5));
        Leggi(riserva, 48);

        riserva.Aggiungi(Byte(20, 6));
        Assert.All(Leggi(riserva, 8), b => Assert.Equal(0, b));

        riserva.Aggiungi(Byte(20, 6));
        Assert.Equal(Byte(8, 6), Leggi(riserva, 8));
    }

    [Fact]
    public void OltreIlMassimoTornaAllObiettivoTenendoICampioniPiuRecenti()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(60, 1));
        riserva.Aggiungi(Byte(48, 2));

        // 108 byte superano il massimo (100): restano i 40 più recenti.
        Assert.Equal(40, riserva.Riserva.TotalMilliseconds, 3);
        Assert.Equal(1, riserva.Tagli);
        Assert.Equal(Byte(40, 2), Leggi(riserva, 40));
    }

    [Fact]
    public void FunzionaAncheQuandoLAnelloRicominciaDaCapo()
    {
        // L'anello contiene 2 secondi (2000 byte): dopo molti giri i dati devono restare corretti.
        var riserva = Crea();
        for (int giro = 0; giro < 200; giro++)
        {
            byte valore = (byte)(giro % 250 + 1);
            riserva.Aggiungi(Byte(48, valore));
            byte[] letto = Leggi(riserva, 48);
            if (giro > 0)
                Assert.Contains(letto, b => b != 0);
        }
        Assert.Equal(0, riserva.Tagli);
    }

    [Fact]
    public void AVelocitaNormaleICampioniEsconoIdentici()
    {
        var riserva = CreaReale();
        byte[] rampa = Rampa(2880); // 60 ms: esattamente l'obiettivo, quindi velocità normale
        riserva.Aggiungi(rampa);

        Assert.Equal(rampa[..400], Leggi(riserva, 400));
        Assert.Equal(1, riserva.Velocita);
    }

    [Fact]
    public void ConLaRiservaTroppoPienaSuonaPiuVeloceERecupera()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(150));

        Leggi(riserva, ByteAlSecondoReali / 1000 * 50);

        // 50 ms suonati all'1,005× consumano 50,25 ms: ne restano 99,75 invece di 100.
        Assert.Equal(1 + RiservaAudio.VariazioneVelocita, riserva.Velocita);
        Assert.InRange(riserva.Riserva.TotalMilliseconds, 99.70, 99.80);
        Assert.Equal(0, riserva.Tagli);
    }

    [Fact]
    public void ConLaRiservaQuasiVuotaSuonaPiuLenta()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(60));
        Leggi(riserva, ByteAlSecondoReali / 1000 * 25);   // la riserva scende a 35 ms

        Leggi(riserva, ByteAlSecondoReali / 1000 * 20);

        // 20 ms suonati allo 0,995× consumano 19,9 ms.
        Assert.Equal(1 - RiservaAudio.VariazioneVelocita, riserva.Velocita);
        Assert.InRange(riserva.Riserva.TotalMilliseconds, 15.05, 15.15);
        Assert.Equal(0, riserva.Buchi);
    }

    [Fact]
    public void RitornataAllObiettivoTornaAVelocitaNormale()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(100));
        Leggi(riserva, 4);
        Assert.True(riserva.Velocita > 1);

        // Con nuovi pacchetti da 20 ms e letture da 20 ms la riserva scende piano verso 60 ms.
        for (int i = 0; i < 20_000 && riserva.Velocita > 1; i++)
        {
            riserva.Aggiungi(Millisecondi(20));
            Leggi(riserva, ByteAlSecondoReali / 1000 * 20);
        }

        // La velocità torna normale quando, appena arrivato un pacchetto, la riserva è sull'obiettivo:
        // dopo l'ultima lettura da 20 ms quindi ne restano circa 60 − 20 = 40.
        Assert.Equal(1, riserva.Velocita);
        Assert.InRange(riserva.Riserva.TotalMilliseconds, 39.5, 40.5);
    }

    [Fact]
    public void LInterpolazioneDaValoriIntermedi()
    {
        // Con la velocità aumentata le uscite cadono tra un campione e l'altro della rampa:
        // devono restare comprese tra i valori vicini e sempre crescenti.
        var riserva = CreaReale();
        riserva.Aggiungi(Rampa(7_200)); // 150 ms
        byte[] uscita = Leggi(riserva, 4000);

        short precedente = short.MinValue;
        for (int i = 0; i < uscita.Length; i += 4)
        {
            short sinistro = BinaryPrimitives.ReadInt16LittleEndian(uscita.AsSpan(i));
            short destro = BinaryPrimitives.ReadInt16LittleEndian(uscita.AsSpan(i + 2));
            Assert.Equal(sinistro, destro);
            Assert.True(sinistro >= precedente);
            precedente = sinistro;
        }
        // 1000 istanti all'1,005×: l'ultimo valore è circa 999 × 1,005 ≈ 1004.
        Assert.InRange(precedente, 1003, 1005);
    }

    [Fact]
    public void DuranteIlRiempimentoNonCEUnIstanteInAscolto()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(20), 1_000_000);
        Leggi(riserva, 400);

        Assert.Null(riserva.IstanteInAscolto(TimeSpan.Zero));
    }

    [Fact]
    public void LIstanteInAscoltoSegueICampioniSuonati()
    {
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(60), 1_000_000);

        Leggi(riserva, ByteAlSecondoReali / 1000 * 20);

        // Suonati 20 ms del pacchetto catturato a 1 000 000 µs: si sente ~1 020 000 (più i pochi
        // microsecondi trascorsi durante il test), e 30 ms prima se l'uscita ha 30 ms di latenza.
        Assert.InRange(riserva.IstanteInAscolto(TimeSpan.Zero)!.Value, 1_020_000, 1_030_000);
        Assert.InRange(riserva.IstanteInAscolto(TimeSpan.FromMilliseconds(30))!.Value, 990_000, 1_000_000);
    }

    [Fact]
    public void LIstanteInAscoltoUsaLAncoraDelPacchettoGiusto()
    {
        // Il terzo pacchetto è stato catturato molto dopo (per esempio dopo una pausa):
        // l'istante deve saltare con lui, non continuare a contare dal primo.
        var riserva = CreaReale();
        riserva.Aggiungi(Millisecondi(20), 1_000_000);
        riserva.Aggiungi(Millisecondi(20), 1_020_000);
        riserva.Aggiungi(Millisecondi(20), 5_000_000);

        Leggi(riserva, ByteAlSecondoReali / 1000 * 50);

        Assert.InRange(riserva.IstanteInAscolto(TimeSpan.Zero)!.Value, 5_010_000, 5_020_000);
    }

    [Fact]
    public void SvuotaRicominciaDalRiempimento()
    {
        var riserva = Crea();
        riserva.Aggiungi(Byte(80, 3));
        Leggi(riserva, 8);

        riserva.Svuota();
        riserva.Aggiungi(Byte(20, 4));

        Assert.Equal(TimeSpan.FromMilliseconds(20), riserva.Riserva);
        Assert.All(Leggi(riserva, 8), b => Assert.Equal(0, b));
    }

    // ---- Riserva adattiva ----

    [Fact]
    public void UnaPausaAlzaLObiettivoMaMaiOltreIlTetto()
    {
        var tempo = new TempoFinto();
        var riserva = CreaAdattiva(tempo);
        riserva.Aggiungi(Millisecondi(20));
        tempo.Adesso += 20;
        riserva.Aggiungi(Millisecondi(20));
        Assert.Equal(60, riserva.Obiettivo.TotalMilliseconds, 3);

        // Una pausa di 70 ms chiede 70 + 20 di tolleranza.
        tempo.Adesso += 70;
        riserva.Aggiungi(Millisecondi(20));
        Assert.Equal(90, riserva.Obiettivo.TotalMilliseconds, 3);

        // Una pausa di 300 ms chiederebbe 320: ci si ferma al tetto.
        tempo.Adesso += 300;
        riserva.Aggiungi(Millisecondi(20));
        Assert.Equal(100, riserva.Obiettivo.TotalMilliseconds, 3);
    }

    [Fact]
    public void DopoUnMinutoSenzaPauseLObiettivoTornaAlMinimo()
    {
        var tempo = new TempoFinto();
        var riserva = CreaAdattiva(tempo);
        riserva.Aggiungi(Millisecondi(20));
        tempo.Adesso += 70;
        riserva.Aggiungi(Millisecondi(20));

        // Pacchetti regolari ogni 20 ms, letti allo stesso ritmo.
        for (int i = 0; i < 2900; i++)   // 58 secondi
        {
            tempo.Adesso += 20;
            riserva.Aggiungi(Millisecondi(20));
            Leggi(riserva, ByteAlSecondoReali / 1000 * 20);
        }
        Assert.Equal(90, riserva.Obiettivo.TotalMilliseconds, 3);

        for (int i = 0; i < 150; i++)    // oltre il minuto
        {
            tempo.Adesso += 20;
            riserva.Aggiungi(Millisecondi(20));
            Leggi(riserva, ByteAlSecondoReali / 1000 * 20);
        }
        Assert.Equal(60, riserva.Obiettivo.TotalMilliseconds, 3);
    }

    [Fact]
    public void ConLObiettivoAlzatoLaRiservaPienaNonVieneAccelerata()
    {
        // Dopo una pausa, 100 ms di riserva sono quelli voluti: niente corsa verso i 60.
        var tempo = new TempoFinto();
        var riserva = CreaAdattiva(tempo);
        riserva.Aggiungi(Millisecondi(20));
        tempo.Adesso += 80;
        riserva.Aggiungi(Millisecondi(80));    // obiettivo 100, riserva 100

        Leggi(riserva, ByteAlSecondoReali / 1000 * 10);

        Assert.Equal(1, riserva.Velocita);
        Assert.Equal(0, riserva.Buchi);
    }

    [Fact]
    public void OltreIlMassimoTornaSubitoAllObiettivoConUnaDissolvenzaIncrociata()
    {
        var tempo = new TempoFinto();
        var riserva = CreaAdattiva(tempo);
        riserva.Aggiungi(Costante(60, 1000));
        Leggi(riserva, ByteAlSecondoReali / 1000 * 10);   // si suona; restano 50 ms da 1000

        // Arrivano tutti insieme 120 ms trattenuti da una pausa: 170 ms superano il massimo (140).
        riserva.Aggiungi(Costante(120, -1000));
        Assert.Equal(60, riserva.Riserva.TotalMilliseconds, 3);
        Assert.Equal(1, riserva.Tagli);

        // I primi 5 ms (240 istanti) passano con continuità da 1000 a −1000, poi solo −1000.
        short[] uscita = Sinistro(Leggi(riserva, ByteAlSecondoReali / 1000 * 10));
        Assert.InRange(uscita[0], 980, 1000);
        for (int i = 1; i < 240; i++)
            Assert.True(uscita[i] < uscita[i - 1], $"l'istante {i} doveva scendere");
        Assert.InRange(uscita[239], -1000, -980);
        Assert.All(uscita[240..], v => Assert.Equal(-1000, v));
    }

    [Fact]
    public void IlSuonoSfumaPrimaDiUnBucoERientraInDissolvenza()
    {
        var tempo = new TempoFinto();
        var riserva = CreaAdattiva(tempo);
        riserva.Aggiungi(Costante(60, 1000));

        short[] uscita = Sinistro(Leggi(riserva, ByteAlSecondoReali / 1000 * 80));

        // Entrata: da quasi zero a 1000 in 240 istanti; uscita: da 1000 a quasi zero prima del silenzio.
        Assert.InRange(uscita[0], 1, 10);
        for (int i = 1; i < 240; i++)
            Assert.True(uscita[i] > uscita[i - 1], $"l'istante {i} doveva salire");
        Assert.All(uscita[240..2640], v => Assert.Equal(1000, v));
        for (int i = 2641; i < 2880; i++)
            Assert.True(uscita[i] < uscita[i - 1], $"l'istante {i} doveva scendere");
        Assert.InRange(uscita[2879], 1, 10);
        Assert.All(uscita[2880..], v => Assert.Equal(0, v));
        Assert.Equal(1, riserva.Buchi);
    }

    [Fact]
    public void BuchiETagliVengonoAnnunciatiConIDettagli()
    {
        var tempo = new TempoFinto();
        var riserva = CreaAdattiva(tempo);
        var eventi = new List<RiservaAudio.Evento>();
        riserva.EventoAvvenuto += eventi.Add;
        const int ms = ByteAlSecondoReali / 1000;

        riserva.Aggiungi(Millisecondi(60));
        Leggi(riserva, ms * 10);                 // si suona; restano 50 ms
        riserva.Aggiungi(Millisecondi(100));     // 150 > 140: taglio mentre si suona
        Leggi(riserva, ms * 80);                 // 60 ms disponibili, 80 chiesti: buco
        riserva.Aggiungi(Millisecondi(160));     // 160 > 140 durante il riempimento

        RiservaAudio.Evento[] attesi =
        [
            new(RiservaAudio.TipoEvento.Taglio, ms * 150, ms * 60, ms * 90, ms * 10),
            new(RiservaAudio.TipoEvento.Buco, ms * 60, ms * 60, ms * 20, ms * 80),
            new(RiservaAudio.TipoEvento.TaglioInRiempimento, ms * 160, ms * 60, ms * 100, ms * 80),
        ];
        Assert.Equal(attesi, eventi);
    }

    [Fact]
    public void SvuotaDimenticaLePauseDellaConnessionePrecedente()
    {
        var tempo = new TempoFinto();
        var riserva = CreaAdattiva(tempo);
        riserva.Aggiungi(Millisecondi(20));
        tempo.Adesso += 300;
        riserva.Aggiungi(Millisecondi(20));
        Assert.Equal(100, riserva.Obiettivo.TotalMilliseconds, 3);

        riserva.Svuota();
        tempo.Adesso += 5000;   // la nuova connessione arriva dopo un po': non è una pausa
        riserva.Aggiungi(Millisecondi(20));

        Assert.Equal(60, riserva.Obiettivo.TotalMilliseconds, 3);
    }
}
