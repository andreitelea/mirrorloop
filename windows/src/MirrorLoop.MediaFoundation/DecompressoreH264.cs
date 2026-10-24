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
    // H.264 lavora a blocchi di 16×16: con misure non multiple di 16 (per esempio 1320×2868, lo
    // schermo di un iPhone 17 Pro Max) il decompressore restituisce l'immagine con un bordo di
    // riempimento (1328×2880) e indica a parte la zona vera. Larghezza e altezza sono quelle della
    // zona vera; l'altezza piena serve a trovare il piano dei colori nel campione.
    private int _larghezza;
    private int _altezza;
    private int _altezzaPiena;
    private int _inizioX;
    private int _inizioY;
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

    /// <summary>
    /// Copia la sola zona vera dell'immagine, senza il bordo di riempimento. Il passo resta quello del
    /// decompressore e il piano dei colori segue subito le righe vere, come in un NV12 senza bordo.
    /// </summary>
    private FrameDecompresso Estrai(IMFSample campione)
    {
        int dimensionePiena = _passo * _altezzaPiena * 3 / 2;
        int dimensione = _passo * _altezza * 3 / 2;
        byte[] nv12 = Campioni.Leggi(campione, contenuto =>
        {
            if (contenuto.Length < dimensionePiena)
                throw new InvalidDataException($"Frame decompresso troppo corto: {contenuto.Length} byte invece di {dimensionePiena}.");
            byte[] buffer = _nuovoBuffer(dimensione);
            if (_altezza == _altezzaPiena && _inizioX == 0)
            {
                contenuto[..dimensione].CopyTo(buffer);
                return buffer;
            }
            // Luminosità: una riga per pixel; colori (U e V alternati): una riga ogni due pixel.
            for (int riga = 0; riga < _altezza; riga++)
                contenuto.Slice((_inizioY + riga) * _passo + _inizioX, _larghezza).CopyTo(buffer.AsSpan(riga * _passo));
            int coloriPieni = _passo * _altezzaPiena;
            int colori = _passo * _altezza;
            for (int riga = 0; riga < _altezza / 2; riga++)
                contenuto.Slice(coloriPieni + (_inizioY / 2 + riga) * _passo + _inizioX, _larghezza).CopyTo(buffer.AsSpan(colori + riga * _passo));
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
                int larghezzaPiena = (int)(dimensioni >> 32);
                _altezzaPiena = (int)(dimensioni & 0xFFFFFFFF);
                // Se il passo non è indicato, ogni riga è lunga esattamente quanto la larghezza.
                _passo = tipo.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint passo).Success && passo > 0
                    ? (int)passo
                    : larghezzaPiena;
                (_inizioX, _inizioY, _larghezza, _altezza) = ZonaVera(tipo, larghezzaPiena, _altezzaPiena);
                _trasformazione.PreparaUscita(_passo * _altezzaPiena * 3 / 2);
                return;
            }
        }
    }

    /// <summary>
    /// La zona vera dell'immagine (MF_MT_MINIMUM_DISPLAY_APERTURE, ricavata dal ritaglio scritto
    /// nell'SPS). Se manca o non è credibile si usa l'immagine intera. Verificato il 24/10/2026: a
    /// 1320×2868 il decompressore Microsoft dà 1328×2880 e indica (0,0) 1320×2868.
    /// </summary>
    private static (int X, int Y, int Larghezza, int Altezza) ZonaVera(IMFMediaType tipo, int larghezzaPiena, int altezzaPiena)
    {
        var intera = (0, 0, larghezzaPiena, altezzaPiena);
        byte[] area;
        try
        {
            area = tipo.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
        }
        catch (SharpGenException)
        {
            return intera;
        }
        // MFVideoArea: OffsetX e OffsetY (MFOffset: parte frazionaria, poi intera, 16 bit ciascuna), poi cx e cy (32 bit).
        if (area.Length < 16)
            return intera;
        int x = BitConverter.ToInt16(area, 2), y = BitConverter.ToInt16(area, 6);
        int larghezza = BitConverter.ToInt32(area, 8), altezza = BitConverter.ToInt32(area, 12);
        // NV12 ha un colore ogni 2×2 pixel: inizio e misure devono essere pari.
        bool credibile = larghezza > 0 && altezza > 0 && x >= 0 && y >= 0
            && x + larghezza <= larghezzaPiena && y + altezza <= altezzaPiena
            && (x | y | larghezza | altezza) % 2 == 0;
        return credibile ? (x, y, larghezza, altezza) : intera;
    }

    public void Dispose() => _trasformazione.Dispose();
}
