using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace MirrorLoop.MediaFoundation;

/// <summary>Cosa ha risposto la trasformazione quando le abbiamo chiesto un'uscita.</summary>
public enum EsitoUscita
{
    /// <summary>C'era un campione pronto, già passato a chi l'ha chiesto.</summary>
    Campione,
    /// <summary>Per ora non c'è altro: serve un nuovo ingresso.</summary>
    ServeIngresso,
    /// <summary>Il formato di uscita è cambiato (tipico del decompressore al primo keyframe): va rinegoziato.</summary>
    CambioFormato,
}

/// <summary>
/// Un componente Media Foundation "sincrono" (compressore o decompressore): si dà un campione
/// in ingresso e si raccolgono quelli in uscita. Qui sta tutta la parte delicata, scritta una volta sola.
/// </summary>
public sealed class Trasformazione : IDisposable
{
    private const int MfENeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MftOutputStreamProvidesSamples = 0x100;
    private const int MftOutputStreamCanProvideSamples = 0x200;

    private bool _forniscePacchetti;
    private int _dimensioneUscita;

    public IMFTransform Mft { get; }

    public Trasformazione(Guid clsid)
    {
        InteropCom.InizializzaCom();
        MediaFactory.MFStartup().CheckError();
        Mft = new IMFTransform(InteropCom.CreaOggetto(clsid, InteropCom.IidIMFTransform));
    }

    /// <summary>
    /// Da chiamare dopo aver impostato i formati (e dopo ogni cambio di formato): legge chi deve
    /// fornire lo spazio per l'uscita. Se tocca a noi, ne diamo almeno <paramref name="dimensioneMinima"/> byte.
    /// </summary>
    public void PreparaUscita(int dimensioneMinima)
    {
        OutputStreamInfo info = Mft.GetOutputStreamInfo(0);
        _forniscePacchetti = ((int)info.Flags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
        _dimensioneUscita = Math.Max(info.Size, dimensioneMinima);
    }

    public void Avvia()
    {
        Mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        Mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public void Inserisci(IMFSample campione) => Mft.ProcessInput(0, campione, 0);

    /// <summary>Fine del flusso: la trasformazione restituisce quello che ha ancora in pancia.</summary>
    public void Svuota()
    {
        Mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        Mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
    }

    /// <summary>
    /// Chiede un campione in uscita. Se c'è, lo passa a <paramref name="usa"/>: il campione è valido
    /// solo durante quella chiamata, poi viene rilasciato.
    /// </summary>
    public EsitoUscita ProvaUscita(Action<IMFSample> usa)
    {
        IMFSample? nostro = null;
        if (!_forniscePacchetti)
        {
            nostro = MediaFactory.MFCreateSample();
            using IMFMediaBuffer spazio = MediaFactory.MFCreateMemoryBuffer(_dimensioneUscita);
            nostro.AddBuffer(spazio);
        }

        var uscita = new OutputDataBuffer { StreamID = 0, Sample = nostro };
        Result esito = Mft.ProcessOutput(ProcessOutputFlags.None, 1, ref uscita, out _);
        uscita.Events?.Dispose();

        // Vortice restituisce un *nuovo* oggetto C# anche quando il campione è il nostro,
        // senza aggiungere un riferimento: va rilasciato una volta sola, altrimenti il conteggio
        // dei riferimenti va sotto zero e il programma si schianta più avanti.
        IMFSample? campione = uscita.Sample;
        bool eIlNostro = campione is not null && nostro is not null && campione.NativePointer == nostro.NativePointer;
        try
        {
            if (esito.Code == MfENeedMoreInput)
                return EsitoUscita.ServeIngresso;
            if (esito.Code == MfETransformStreamChange)
                return EsitoUscita.CambioFormato;
            esito.CheckError();

            if (campione is null)
                return EsitoUscita.ServeIngresso;
            usa(campione);
            return EsitoUscita.Campione;
        }
        finally
        {
            nostro?.Dispose();
            if (!eIlNostro)
                campione?.Dispose();
        }
    }

    public void Dispose()
    {
        Mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        Mft.Dispose();
        MediaFactory.MFShutdown();
    }
}
