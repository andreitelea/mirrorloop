using Vortice.MediaFoundation;

namespace MirrorLoop.MediaFoundation;

/// <summary>Piccoli aiuti per creare e leggere i campioni (IMFSample) di Media Foundation.</summary>
public static class Campioni
{
    /// <summary>Media Foundation misura il tempo in unità da 100 nanosecondi.</summary>
    public const long UnitaPerSecondo = 10_000_000;

    /// <summary>Molti attributi video sono coppie di numeri a 32 bit messe in un unico numero a 64 bit.</summary>
    public static ulong Coppia(uint alto, uint basso) => (ulong)alto << 32 | basso;

    /// <summary>Crea un campione con una copia dei dati.</summary>
    public static IMFSample Crea(ReadOnlySpan<byte> dati, long tempo, long durata)
    {
        IMFSample campione = MediaFactory.MFCreateSample();
        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(dati.Length);
        buffer.Lock(out IntPtr memoria, out _, out _);
        try
        {
            dati.CopyTo(Span(memoria, dati.Length));
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = dati.Length;
        campione.AddBuffer(buffer);
        campione.SampleTime = tempo;
        campione.SampleDuration = durata;
        return campione;
    }

    /// <summary>Legge il contenuto del campione. I dati passati a <paramref name="leggi"/> valgono solo durante la chiamata.</summary>
    public static T Leggi<T>(IMFSample campione, Func<ReadOnlySpan<byte>, T> leggi)
    {
        using IMFMediaBuffer buffer = campione.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr memoria, out _, out int lunghezza);
        try
        {
            return leggi(Span(memoria, lunghezza));
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>
    /// "Clean point" è il modo in cui Media Foundation dice: questo frame si decodifica da solo.
    /// Se l'attributo manca, il frame non è un keyframe.
    /// </summary>
    public static bool EKeyframe(IMFSample campione) =>
        campione.GetUInt32(SampleAttributeKeys.CleanPoint, out uint pulito).Success && pulito != 0;

    private static unsafe Span<byte> Span(IntPtr puntatore, int lunghezza) => new((void*)puntatore, lunghezza);
}
