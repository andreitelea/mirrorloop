using System.Runtime.InteropServices;

namespace MirrorLoop.MediaFoundation;

/// <summary>
/// Le poche parti di COM che Vortice non espone. GUID e ordine dei metodi presi
/// dal Windows SDK (codecapi.h, strmif.h, wmcodecdsp.h).
/// </summary>
public static class InteropCom
{
    /// <summary>CLSID_CMSH264EncoderMFT: il compressore H.264 software incluso in Windows.</summary>
    public static readonly Guid ClsidCompressoreH264Microsoft = new("6ca50344-051a-4ded-9779-a43305165e35");

    /// <summary>CLSID_CMSH264DecoderMFT: il decompressore H.264 software incluso in Windows.</summary>
    public static readonly Guid ClsidDecompressoreH264Microsoft = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");

    public static readonly Guid IidIMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    // Parametri del compressore (codecapi.h).
    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    /// <summary>eAVEncCommonRateControlMode_CBR: bitrate costante.</summary>
    public const uint RateControlCbr = 0;

    private const uint ClsctxInprocServer = 1;
    private const uint CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr riservato, uint modalita);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr esterno, uint contesto, in Guid iid, out IntPtr oggetto);

    /// <summary>
    /// COM va inizializzato su ogni thread che lo usa. "Changed mode" vuol dire che il thread
    /// era già inizializzato in un'altra modalità: va bene lo stesso.
    /// </summary>
    public static void InizializzaCom()
    {
        int hr = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        if (hr < 0 && hr != RpcEChangedMode)
            Marshal.ThrowExceptionForHR(hr);
    }

    public static IntPtr CreaOggetto(Guid clsid, Guid iid)
    {
        int hr = CoCreateInstance(clsid, IntPtr.Zero, ClsctxInprocServer, iid, out IntPtr oggetto);
        Marshal.ThrowExceptionForHR(hr);
        return oggetto;
    }

    /// <summary>
    /// Imposta un parametro del compressore tramite ICodecAPI. Restituisce false se il compressore
    /// non lo supporta, così chi chiama decide se è grave.
    /// </summary>
    public static bool ImpostaParametro(IntPtr compressore, Guid parametro, object valore)
    {
        object rcw = Marshal.GetObjectForIUnknown(compressore);
        try
        {
            if (rcw is not ICodecAPI api)
                return false;

            if (api.IsSupported(in parametro) != 0)
                return false;

            return api.SetValue(in parametro, ref valore) >= 0;
        }
        finally
        {
            Marshal.ReleaseComObject(rcw);
        }
    }

    // Solo i metodi fino a SetValue: l'ordine deve combaciare con la tabella dei metodi in strmif.h.
    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported(in Guid api);
        [PreserveSig] int IsModifiable(in Guid api);
        [PreserveSig] int GetParameterRange(in Guid api, IntPtr minimo, IntPtr massimo, IntPtr passo);
        [PreserveSig] int GetParameterValues(in Guid api, IntPtr valori, IntPtr quanti);
        [PreserveSig] int GetDefaultValue(in Guid api, IntPtr valore);
        [PreserveSig] int GetValue(in Guid api, IntPtr valore);
        [PreserveSig] int SetValue(in Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object valore);
    }
}
