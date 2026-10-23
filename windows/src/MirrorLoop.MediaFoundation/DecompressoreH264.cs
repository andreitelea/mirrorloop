using SharpGen.Runtime;
using Vortice;
using Vortice.MediaFoundation;

namespace MirrorLoop.MediaFoundation;

/// <summary>
/// Un'immagine decompressa in formato NV12. <see cref="Passo"/> (in inglese "stride") è la lunghezza
/// in byte di una riga in memoria: può essere più grande della larghezza, per allineamento.
/// </summary>
public sealed record FrameDecompresso(byte[] Nv12, int Larghezza, int Altezza, int Passo, long Tempo);

/// <summary>
/// Decompressore H.264 basato su Media Foundation (il decompressore software di Microsoft).
/// Riceve frame H.264 Annex B e restituisce immagini NV12.
/// </summary>
public sealed class DecompressoreH264 : IDisposable
{
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);

    private readonly Trasformazione _trasformazione;
    private readonly Func<int, byte[]> _nuovoBuffer;
    private readonly Guid _nv12 = VideoFormatGuids.FromFourCC(new FourCC("NV12"));
    private int _larghezza;
    private int _altezza;
    private int _passo;

    /// <param name="nuovoBuffer">
    /// Da dove prendere il buffer per un'immagine di N byte (al massimo N byte vengono scritti). Di
    /// norma un array nuovo; chi decomprime a 60 fps può passare una riserva di buffer da riusare,
    /// per non creare centinaia di MB al secondo di memoria da raccogliere.
    /// </param>
    public DecompressoreH264(Func<int, byte[]>? nuovoBuffer = null)
    {
        _nuovoBuffer = nuovoBuffer ?? (dimensione => new byte[dimensione]);
        _trasformazione = new Trasformazione(InteropCom.ClsidDecompressoreH264Microsoft);
        IMFTransform mft = _trasformazione.Mft;

        // Senza bassa latenza il decompressore tiene da parte alcuni frame prima di restituirli.
        // Il decompressore Microsoft la accetta come attributo (MF_LOW_LATENCY, stesso GUID di
        // CODECAPI_AVLowLatencyMode), non tramite ICodecAPI. L'effetto è verificato dai test.
        if (!InteropCom.ImpostaParametro(mft.NativePointer, InteropCom.AVLowLatencyMode, true))
        {
            using IMFAttributes attributi = mft.Attributes;
            attributi.Set(InteropCom.AVLowLatencyMode, 1u);
        }

        // Per il decompressore basta dire "H.264": dimensioni e resto li legge dall'SPS del primo keyframe.
        using (IMFMediaType ingresso = MediaFactory.MFCreateMediaType())
        {
            ingresso.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            ingresso.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.FromFourCC(new FourCC("H264")));
            mft.SetInputType(0, ingresso, 0);
        }

        ScegliUscitaNv12();
        _trasformazione.Avvia();
    }

    /// <summary>
    /// Decomprime un frame H.264. <paramref name="tempo"/> viaggia insieme al frame e torna uguale
    /// nel frame decompresso: lo usiamo per l'istante di cattura, così possiamo misurare la latenza.
    /// </summary>
    public List<FrameDecompresso> Decomprimi(ReadOnlySpan<byte> datiH264, long tempo)
    {
        using (IMFSample campione = Campioni.Crea(datiH264, tempo, 0))
            _trasformazione.Inserisci(campione);
        return RaccogliUscita();
    }

    /// <summary>Fine del flusso: restituisce i frame che il decompressore aveva ancora da parte.</summary>
    public List<FrameDecompresso> Concludi()
    {
        _trasformazione.Svuota();
        return RaccogliUscita();
    }

    private List<FrameDecompresso> RaccogliUscita()
    {
        var risultati = new List<FrameDecompresso>();
        while (true)
        {
            EsitoUscita esito = _trasformazione.ProvaUscita(campione => risultati.Add(Estrai(campione)));
            if (esito == EsitoUscita.CambioFormato)
            {
                // Al primo keyframe il decompressore scopre le dimensioni vere e chiede di rinegoziare.
                ScegliUscitaNv12();
                continue;
            }
            if (esito == EsitoUscita.ServeIngresso)
                return risultati;
        }
    }

    private FrameDecompresso Estrai(IMFSample campione)
    {
        int dimensione = _passo * _altezza * 3 / 2;
        byte[] nv12 = Campioni.Leggi(campione, contenuto =>
        {
            if (contenuto.Length < dimensione)
                throw new InvalidDataException($"Frame decompresso troppo corto: {contenuto.Length} byte invece di {dimensione}.");
            byte[] buffer = _nuovoBuffer(dimensione);
            contenuto[..dimensione].CopyTo(buffer);
            return buffer;
        });
        return new FrameDecompresso(nv12, _larghezza, _altezza, _passo, campione.SampleTime);
    }

    /// <summary>Tra i formati di uscita offerti dal decompressore sceglie NV12 e ne legge dimensioni e passo.</summary>
    private void ScegliUscitaNv12()
    {
        IMFTransform mft = _trasformazione.Mft;
        for (int indice = 0; ; indice++)
        {
            IMFMediaType tipo;
            try
            {
                tipo = mft.GetOutputAvailableType(0, indice);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MfENoMoreTypes)
            {
                throw new InvalidOperationException("Il decompressore non offre il formato NV12.");
            }

            using (tipo)
            {
                if (tipo.GetGUID(MediaTypeAttributeKeys.Subtype) != _nv12)
                    continue;

                mft.SetOutputType(0, tipo, 0);
                ulong dimensioni = tipo.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                _larghezza = (int)(dimensioni >> 32);
                _altezza = (int)(dimensioni & 0xFFFFFFFF);
                // Se il passo non è indicato, ogni riga è lunga esattamente quanto la larghezza.
                _passo = tipo.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint passo).Success && passo > 0
                    ? (int)passo
                    : _larghezza;
                _trasformazione.PreparaUscita(_passo * _altezza * 3 / 2);
                return;
            }
        }
    }

    public void Dispose() => _trasformazione.Dispose();
}
