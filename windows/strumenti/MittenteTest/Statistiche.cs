namespace MittenteTest;

/// <summary>
/// Conta e controlla i frame compressi. Guarda anche "dentro" i dati H.264 per verificare
/// che ogni keyframe porti con sé SPS e PPS, come richiede docs/protocollo.md.
/// </summary>
public sealed class Statistiche(int fps)
{
    // Tipi di NAL unit H.264 che ci interessano (ITU-T H.264, tabella 7-1).
    private const int NalIdr = 5;
    private const int NalSps = 7;
    private const int NalPps = 8;

    private long _frame;
    private long _keyframe;
    private long _byteTotali;
    private long _byteKeyframe;
    private long _keyframeDichiaratiSenzaIdr;
    private long _idrNonDichiarati;
    private readonly List<long> _posizioniKeyframe = [];

    public long KeyframeSenzaIstruzioni { get; private set; }

    public void Aggiungi(FrameCompresso f)
    {
        HashSet<int> tipi = TipiNal(f.DatiH264);
        bool idr = tipi.Contains(NalIdr);

        _frame++;
        _byteTotali += f.DatiH264.Length;

        if (f.Keyframe)
        {
            _keyframe++;
            _byteKeyframe += f.DatiH264.Length;
            _posizioniKeyframe.Add(f.Numero);
            if (!tipi.Contains(NalSps) || !tipi.Contains(NalPps))
                KeyframeSenzaIstruzioni++;
            if (!idr)
                _keyframeDichiaratiSenzaIdr++;
        }
        else if (idr)
        {
            _idrNonDichiarati++;
        }
    }

    public void StampaRiepilogo(TimeSpan durata, long frameGenerati)
    {
        double secondiVideo = (double)_frame / fps;
        Console.WriteLine();
        Console.WriteLine($"Frame generati: {frameGenerati} in {durata.TotalSeconds:F2} s ({frameGenerati / durata.TotalSeconds:F1} fps reali)");
        Console.WriteLine($"Frame compressi: {_frame}, di cui keyframe: {_keyframe}");
        Console.WriteLine($"Keyframe ai frame: {string.Join(", ", _posizioniKeyframe.Take(10))}{(_posizioniKeyframe.Count > 10 ? ", ..." : "")}");
        Console.WriteLine($"Bitrate medio: {_byteTotali * 8 / secondiVideo / 1_000_000:F2} Mbit/s");
        if (_keyframe > 0 && _frame > _keyframe)
            Console.WriteLine($"Dimensione media: keyframe {_byteKeyframe / _keyframe / 1024.0:F1} KB, altri {(_byteTotali - _byteKeyframe) / (_frame - _keyframe) / 1024.0:F1} KB");
        Console.WriteLine($"Keyframe senza SPS/PPS: {KeyframeSenzaIstruzioni}");
        Console.WriteLine($"Incoerenze (keyframe senza IDR / IDR non segnalati): {_keyframeDichiaratiSenzaIdr} / {_idrNonDichiarati}");
    }

    /// <summary>
    /// Cerca i codici di inizio Annex B (00 00 01, eventualmente preceduto da un altro 00)
    /// e legge il tipo di ogni NAL unit dai 5 bit bassi del byte successivo.
    /// </summary>
    private static HashSet<int> TipiNal(byte[] dati)
    {
        var tipi = new HashSet<int>();
        for (int i = 0; i + 3 < dati.Length; i++)
        {
            if (dati[i] == 0 && dati[i + 1] == 0 && dati[i + 2] == 1)
            {
                tipi.Add(dati[i + 3] & 0x1F);
                i += 2;
            }
        }
        return tipi;
    }
}
