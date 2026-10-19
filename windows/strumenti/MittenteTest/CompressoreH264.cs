using MirrorLoop.MediaFoundation;
using Vortice;
using Vortice.MediaFoundation;

namespace MittenteTest;

/// <summary>Un frame compresso in uscita dal compressore.</summary>
public sealed record FrameCompresso(byte[] DatiH264, bool Keyframe, long Numero);

/// <summary>
/// Compressore H.264 basato su Media Foundation, il motore video di Windows.
/// Per ora usa il compressore software di Microsoft, presente su ogni PC Windows:
/// funziona in modo "sincrono" (dai un frame, chiedi l'uscita), più semplice di quelli hardware.
/// </summary>
public sealed class CompressoreH264 : IDisposable
{
    private const uint InterlacciamentoProgressivo = 2;
    private const uint ProfiloBaseline = 66;

    private readonly Trasformazione _trasformazione;
    private readonly int _dimensioneFrame;
    private readonly long _durataFrame;
    private long _frameInIngresso;
    private long _frameInUscita;

    public string Descrizione { get; }

    public CompressoreH264(int larghezza, int altezza, int fps, int bitrate, int intervalloKeyframe)
    {
        _dimensioneFrame = larghezza * altezza * 3 / 2;
        _durataFrame = Campioni.UnitaPerSecondo / fps;
        _trasformazione = new Trasformazione(InteropCom.ClsidCompressoreH264Microsoft);
        IMFTransform mft = _trasformazione.Mft;

        // I parametri del compressore vanno impostati prima dei formati.
        var impostati = new List<string>();
        var ignorati = new List<string>();
        void Imposta(string nome, Guid parametro, object valore) =>
            (InteropCom.ImpostaParametro(mft.NativePointer, parametro, valore) ? impostati : ignorati).Add(nome);

        Imposta("bitrate costante", InteropCom.AVEncCommonRateControlMode, InteropCom.RateControlCbr);
        Imposta("bitrate", InteropCom.AVEncCommonMeanBitRate, (uint)bitrate);
        Imposta("keyframe ogni N frame", InteropCom.AVEncMPVGOPSize, (uint)intervalloKeyframe);
        Imposta("niente B-frame", InteropCom.AVEncMPVDefaultBPictureCount, 0u);
        Imposta("bassa latenza", InteropCom.AVLowLatencyMode, true);

        // Per i compressori va impostato prima il formato di uscita, poi quello di ingresso.
        using (IMFMediaType uscita = MediaFactory.MFCreateMediaType())
        {
            uscita.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            uscita.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.FromFourCC(new FourCC("H264")));
            uscita.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            uscita.Set(MediaTypeAttributeKeys.FrameSize, Campioni.Coppia((uint)larghezza, (uint)altezza));
            uscita.Set(MediaTypeAttributeKeys.FrameRate, Campioni.Coppia((uint)fps, 1));
            uscita.Set(MediaTypeAttributeKeys.PixelAspectRatio, Campioni.Coppia(1, 1));
            uscita.Set(MediaTypeAttributeKeys.InterlaceMode, InterlacciamentoProgressivo);
            // Baseline non prevede B-frame: una garanzia in più per la bassa latenza.
            uscita.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfiloBaseline);
            mft.SetOutputType(0, uscita, 0);
        }

        using (IMFMediaType ingresso = MediaFactory.MFCreateMediaType())
        {
            ingresso.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            ingresso.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.FromFourCC(new FourCC("NV12")));
            ingresso.Set(MediaTypeAttributeKeys.FrameSize, Campioni.Coppia((uint)larghezza, (uint)altezza));
            ingresso.Set(MediaTypeAttributeKeys.FrameRate, Campioni.Coppia((uint)fps, 1));
            ingresso.Set(MediaTypeAttributeKeys.PixelAspectRatio, Campioni.Coppia(1, 1));
            ingresso.Set(MediaTypeAttributeKeys.InterlaceMode, InterlacciamentoProgressivo);
            mft.SetInputType(0, ingresso, 0);
        }

        // Un frame compresso non supera mai quello non compresso: è lo spazio che offriamo in uscita.
        _trasformazione.PreparaUscita(_dimensioneFrame);
        _trasformazione.Avvia();

        Descrizione = $"compressore software Microsoft · parametri impostati: {string.Join(", ", impostati)}"
            + (ignorati.Count > 0 ? $" · NON supportati: {string.Join(", ", ignorati)}" : "");
    }

    /// <summary>Comprime un frame NV12. Restituisce i frame compressi pronti (zero, uno o più).</summary>
    public List<FrameCompresso> Comprimi(ReadOnlySpan<byte> frameNv12)
    {
        if (frameNv12.Length != _dimensioneFrame)
            throw new ArgumentException($"Il frame deve essere di {_dimensioneFrame} byte.", nameof(frameNv12));

        using (IMFSample campione = Campioni.Crea(frameNv12, _frameInIngresso * _durataFrame, _durataFrame))
            _trasformazione.Inserisci(campione);
        _frameInIngresso++;

        return RaccogliUscita();
    }

    /// <summary>Svuota il compressore a fine trasmissione e restituisce gli ultimi frame.</summary>
    public List<FrameCompresso> Concludi()
    {
        _trasformazione.Svuota();
        return RaccogliUscita();
    }

    private List<FrameCompresso> RaccogliUscita()
    {
        var risultati = new List<FrameCompresso>();
        while (_trasformazione.ProvaUscita(campione => risultati.Add(Estrai(campione))) == EsitoUscita.Campione)
        {
        }
        return risultati;
    }

    private FrameCompresso Estrai(IMFSample campione)
    {
        // Il compressore Microsoft mette un AUD (Access Unit Delimiter) davanti a ogni frame.
        // Per noi è inutile, perché ogni messaggio contiene già un frame intero,
        // e un AUD all'inizio di un file impedisce a VLC 3 di riconoscerlo come H.264.
        byte[] dati = Campioni.Leggi(campione, contenuto => AnnexB.TogliNal(contenuto, AnnexB.NalAud));
        return new FrameCompresso(dati, Campioni.EKeyframe(campione), _frameInUscita++);
    }

    public void Dispose() => _trasformazione.Dispose();
}
