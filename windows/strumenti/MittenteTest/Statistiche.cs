using MirrorLoop.MediaFoundation;

namespace MittenteTest;

/// <summary>
/// Conta e controlla i frame compressi. Guarda anche "dentro" i dati H.264 per verificare
/// che ogni keyframe porti con sé SPS e PPS, come richiede docs/protocollo.md.
/// </summary>
public sealed class Statistiche(int fps)
{
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
        HashSet<int> tipi = AnnexB.TipiNal(f.DatiH264);
        bool idr = tipi.Contains(AnnexB.NalIdr);

        _frame++;
        _byteTotali += f.DatiH264.Length;

        if (f.Keyframe)
        {
            _keyframe++;
            _byteKeyframe += f.DatiH264.Length;
            _posizioniKeyframe.Add(f.Numero);
            if (!tipi.Contains(AnnexB.NalSps) || !tipi.Contains(AnnexB.NalPps))
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
}
