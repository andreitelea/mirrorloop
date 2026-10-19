namespace MirrorLoop.MediaFoundation;

/// <summary>
/// Strumenti per l'H.264 in formato Annex B: una sequenza di NAL unit, ognuna preceduta
/// da un codice di inizio 00 00 01 oppure 00 00 00 01.
/// </summary>
public static class AnnexB
{
    // Tipi di NAL unit (ITU-T H.264, tabella 7-1).
    public const int NalIdr = 5;
    public const int NalSei = 6;
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalAud = 9;

    /// <summary>Posizione del prossimo codice di inizio (00 00 01 oppure 00 00 00 01) da <paramref name="da"/> in poi, o -1.</summary>
    public static int TrovaCodiceInizio(ReadOnlySpan<byte> dati, int da)
    {
        for (int i = da; i + 2 < dati.Length; i++)
        {
            if (dati[i] == 0 && dati[i + 1] == 0)
            {
                if (dati[i + 2] == 1)
                    return i;
                if (i + 3 < dati.Length && dati[i + 2] == 0 && dati[i + 3] == 1)
                    return i;
            }
        }
        return -1;
    }

    /// <summary>I tipi di NAL presenti nei dati: i 5 bit bassi del byte dopo ogni codice di inizio.</summary>
    public static HashSet<int> TipiNal(ReadOnlySpan<byte> dati)
    {
        var tipi = new HashSet<int>();
        foreach (var (inizio, lunghezzaCodice, _) in Nal(dati))
        {
            if (inizio + lunghezzaCodice < dati.Length)
                tipi.Add(dati[inizio + lunghezzaCodice] & 0x1F);
        }
        return tipi;
    }

    /// <summary>
    /// Copia i dati togliendo i NAL del tipo indicato (codice di inizio compreso).
    /// Eventuali byte prima del primo codice di inizio non fanno parte di nessun NAL e vengono scartati.
    /// </summary>
    public static byte[] TogliNal(ReadOnlySpan<byte> dati, int tipoDaTogliere)
    {
        var risultato = new List<byte>(dati.Length);
        foreach (var (inizio, lunghezzaCodice, fine) in Nal(dati))
        {
            int tipo = inizio + lunghezzaCodice < dati.Length ? dati[inizio + lunghezzaCodice] & 0x1F : -1;
            if (tipo != tipoDaTogliere)
                risultato.AddRange(dati[inizio..fine]);
        }
        return [.. risultato];
    }

    /// <summary>Per ogni NAL: dove inizia il codice di inizio, quanto è lungo (3 o 4) e dove finisce il NAL.</summary>
    private static List<(int Inizio, int LunghezzaCodice, int Fine)> Nal(ReadOnlySpan<byte> dati)
    {
        var nal = new List<(int, int, int)>();
        int inizio = TrovaCodiceInizio(dati, 0);
        while (inizio >= 0)
        {
            int lunghezzaCodice = dati[inizio + 2] == 1 ? 3 : 4;
            int prossimo = TrovaCodiceInizio(dati, inizio + lunghezzaCodice);
            nal.Add((inizio, lunghezzaCodice, prossimo >= 0 ? prossimo : dati.Length));
            inizio = prossimo;
        }
        return nal;
    }
}
