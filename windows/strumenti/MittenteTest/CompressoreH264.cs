using System.Runtime.InteropServices;
using SharpGen.Runtime;
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
    private const int MfENeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MftOutputStreamProvidesSamples = 0x100;
    private const int MftOutputStreamCanProvideSamples = 0x200;
    private const uint InterlacciamentoProgressivo = 2;
    private const uint ProfiloBaseline = 66;

    /// <summary>Media Foundation misura il tempo in unità da 100 nanosecondi.</summary>
    private const long UnitaPerSecondo = 10_000_000;

    private readonly IMFTransform _mft;
    private readonly int _dimensioneFrame;
    private readonly long _durataFrame;
    private readonly bool _mftForniscePacchetti;
    private readonly int _dimensioneUscita;
    private long _frameInIngresso;
    private long _frameInUscita;

    public string Descrizione { get; }

    public CompressoreH264(int larghezza, int altezza, int fps, int bitrate, int intervalloKeyframe)
    {
        _dimensioneFrame = larghezza * altezza * 3 / 2;
        _durataFrame = UnitaPerSecondo / fps;

        InteropCom.InizializzaCom();
        MediaFactory.MFStartup().CheckError();

        IntPtr puntatore = InteropCom.CreaOggetto(InteropCom.ClsidCompressoreH264Microsoft, InteropCom.IidIMFTransform);
        _mft = new IMFTransform(puntatore);

        // I parametri del compressore vanno impostati prima dei formati.
        var impostati = new List<string>();
        var ignorati = new List<string>();
        void Imposta(string nome, Guid parametro, object valore) =>
            (InteropCom.ImpostaParametro(_mft.NativePointer, parametro, valore) ? impostati : ignorati).Add(nome);

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
            uscita.Set(MediaTypeAttributeKeys.FrameSize, Coppia((uint)larghezza, (uint)altezza));
            uscita.Set(MediaTypeAttributeKeys.FrameRate, Coppia((uint)fps, 1));
            uscita.Set(MediaTypeAttributeKeys.PixelAspectRatio, Coppia(1, 1));
            uscita.Set(MediaTypeAttributeKeys.InterlaceMode, InterlacciamentoProgressivo);
            // Baseline non prevede B-frame: una garanzia in più per la bassa latenza.
            uscita.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfiloBaseline);
            _mft.SetOutputType(0, uscita, 0);
        }

        using (IMFMediaType ingresso = MediaFactory.MFCreateMediaType())
        {
            ingresso.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            ingresso.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.FromFourCC(new FourCC("NV12")));
            ingresso.Set(MediaTypeAttributeKeys.FrameSize, Coppia((uint)larghezza, (uint)altezza));
            ingresso.Set(MediaTypeAttributeKeys.FrameRate, Coppia((uint)fps, 1));
            ingresso.Set(MediaTypeAttributeKeys.PixelAspectRatio, Coppia(1, 1));
            ingresso.Set(MediaTypeAttributeKeys.InterlaceMode, InterlacciamentoProgressivo);
            _mft.SetInputType(0, ingresso, 0);
        }

        OutputStreamInfo info = _mft.GetOutputStreamInfo(0);
        _mftForniscePacchetti = ((int)info.Flags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
        // Se tocca a noi fornire lo spazio per l'uscita, ne diamo in abbondanza:
        // un frame compresso non supera mai quello non compresso.
        _dimensioneUscita = Math.Max(info.Size, _dimensioneFrame);

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        Descrizione = $"compressore software Microsoft · parametri impostati: {string.Join(", ", impostati)}"
            + (ignorati.Count > 0 ? $" · NON supportati: {string.Join(", ", ignorati)}" : "");
    }

    /// <summary>Comprime un frame NV12. Restituisce i frame compressi pronti (zero, uno o più).</summary>
    public List<FrameCompresso> Comprimi(ReadOnlySpan<byte> frameNv12)
    {
        if (frameNv12.Length != _dimensioneFrame)
            throw new ArgumentException($"Il frame deve essere di {_dimensioneFrame} byte.", nameof(frameNv12));

        using IMFSample campione = MediaFactory.MFCreateSample();
        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(_dimensioneFrame);
        buffer.Lock(out IntPtr dati, out _, out _);
        try
        {
            frameNv12.CopyTo(Span(dati, _dimensioneFrame));
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = _dimensioneFrame;
        campione.AddBuffer(buffer);
        campione.SampleTime = _frameInIngresso * _durataFrame;
        campione.SampleDuration = _durataFrame;
        _frameInIngresso++;

        _mft.ProcessInput(0, campione, 0);
        return RaccogliUscita();
    }

    /// <summary>Svuota il compressore a fine trasmissione e restituisce gli ultimi frame.</summary>
    public List<FrameCompresso> Concludi()
    {
        _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        return RaccogliUscita();
    }

    private List<FrameCompresso> RaccogliUscita()
    {
        var risultati = new List<FrameCompresso>();
        while (true)
        {
            IMFSample? nostro = null;
            if (!_mftForniscePacchetti)
            {
                nostro = MediaFactory.MFCreateSample();
                using IMFMediaBuffer spazio = MediaFactory.MFCreateMemoryBuffer(_dimensioneUscita);
                nostro.AddBuffer(spazio);
            }

            var uscita = new OutputDataBuffer { StreamID = 0, Sample = nostro };
            Result esito = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref uscita, out _);
            uscita.Events?.Dispose();

            // Vortice restituisce un *nuovo* oggetto C# anche quando il campione è il nostro,
            // senza aggiungere un riferimento: va rilasciato una volta sola, altrimenti il conteggio
            // dei riferimenti va sotto zero e il programma si schianta più avanti.
            IMFSample? campione = uscita.Sample;
            bool eIlNostro = campione is not null && nostro is not null && campione.NativePointer == nostro.NativePointer;
            try
            {
                // "Serve altro input": per ora il compressore non ha altro da darci.
                if (esito.Code == MfENeedMoreInput)
                    return risultati;
                esito.CheckError();

                if (campione is not null)
                    risultati.Add(Estrai(campione));
            }
            finally
            {
                nostro?.Dispose();
                if (!eIlNostro)
                    campione?.Dispose();
            }
        }
    }

    private FrameCompresso Estrai(IMFSample campione)
    {
        using IMFMediaBuffer buffer = campione.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr dati, out _, out int lunghezza);
        byte[] copia;
        try
        {
            copia = TogliDelimitatori(Span(dati, lunghezza));
        }
        finally
        {
            buffer.Unlock();
        }

        // "Clean point" è il modo in cui Media Foundation dice: questo frame si decodifica da solo.
        // Se l'attributo manca, il frame non è un keyframe.
        bool keyframe = campione.GetUInt32(SampleAttributeKeys.CleanPoint, out uint pulito).Success && pulito != 0;
        return new FrameCompresso(copia, keyframe, _frameInUscita++);
    }

    /// <summary>
    /// Toglie i NAL "Access Unit Delimiter" (tipo 9), che il compressore Microsoft mette davanti
    /// a ogni frame. Per noi sono inutili, perché ogni messaggio contiene già un frame intero,
    /// e un AUD all'inizio di un file impedisce a VLC 3 di riconoscerlo come H.264.
    /// </summary>
    private static byte[] TogliDelimitatori(ReadOnlySpan<byte> dati)
    {
        const int NalAud = 9;
        var risultato = new List<byte>(dati.Length);
        int inizio = TrovaCodiceInizio(dati, 0);
        while (inizio >= 0)
        {
            int lunghezzaCodice = dati[inizio + 2] == 1 ? 3 : 4;
            int prossimo = TrovaCodiceInizio(dati, inizio + lunghezzaCodice);
            int fine = prossimo >= 0 ? prossimo : dati.Length;
            int tipo = inizio + lunghezzaCodice < dati.Length ? dati[inizio + lunghezzaCodice] & 0x1F : -1;
            if (tipo != NalAud)
                risultato.AddRange(dati[inizio..fine]);
            inizio = prossimo;
        }
        return [.. risultato];
    }

    /// <summary>Posizione del prossimo codice di inizio (00 00 01 oppure 00 00 00 01), o -1.</summary>
    private static int TrovaCodiceInizio(ReadOnlySpan<byte> dati, int da)
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

    private static ulong Coppia(uint alto, uint basso) => (ulong)alto << 32 | basso;

    private static unsafe Span<byte> Span(IntPtr puntatore, int lunghezza) => new((void*)puntatore, lunghezza);

    public void Dispose()
    {
        _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        _mft.Dispose();
        MediaFactory.MFShutdown();
    }
}
