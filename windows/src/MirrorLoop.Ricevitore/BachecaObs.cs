using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Scrive immagini (NV12) e audio (PCM 16 bit) nella memoria condivisa da cui li legge la sorgente
/// MirrorLoop dentro OBS. Il formato è descritto in docs/memoria-condivisa.md: ogni modifica va
/// fatta lì, qui e nel plugin insieme.
///
/// Non aspetta mai il lettore: le immagini ruotano su pochi cassetti e l'audio su un nastro
/// circolare, e chi legge in ritardo perde dati invece di rallentare il ricevitore.
///
/// Thread-safe: immagini, audio e battito possono arrivare da thread diversi.
/// </summary>
public sealed unsafe class BachecaObs : IDisposable
{
    public const string NomePredefinito = @"Local\MirrorLoopOBS";
    public const uint Versione = 1;

    public const int DimensioneIntestazione = 4096;
    public const int NumeroCassetti = 3;
    public const int DimensioneCassetto = 8 * 1024 * 1024;
    public const int NumeroBlocchiAudio = 128;
    public const int DimensioneBloccoAudio = 4096;
    public const int IntestazioneElemento = 64;
    public const long DimensioneTotale =
        DimensioneIntestazione + (long)NumeroCassetti * DimensioneCassetto + (long)NumeroBlocchiAudio * DimensioneBloccoAudio;

    public const uint FormatoNv12 = 1;
    public const uint ColoreBt709Limitato = 1;
    public const uint FormatoPcm16 = 1;

    // Posizioni nell'intestazione.
    internal const int PosFirma = 0, PosVersione = 4, PosDimensione = 8, PosBattito = 16, PosGenerazione = 24,
        PosImmaginiScritte = 32, PosBlocchiScritti = 40, PosNumeroCassetti = 48, PosDimensioneCassetto = 52,
        PosNumeroBlocchi = 56, PosDimensioneBlocco = 60, PosProcesso = 64;

    // Posizioni dentro un cassetto o un blocco (le prime quattro sono comuni).
    internal const int PosSequenza = 0, PosNumero = 8, PosIstante = 16, PosGenerazioneElemento = 24;
    internal const int PosLarghezza = 32, PosAltezza = 36, PosPassoY = 40, PosPassoUv = 44, PosFormatoImmagine = 48,
        PosColore = 52, PosByteImmagine = 56;
    internal const int PosCampioni = 32, PosCanali = 36, PosFrequenza = 40, PosFormatoAudio = 44, PosByteAudio = 48;

    private static ReadOnlySpan<byte> Firma => "MLOB"u8;

    private readonly MemoryMappedFile _memoria;
    private readonly MemoryMappedViewAccessor _vista;
    private readonly EventWaitHandle _nuovo;
    private readonly byte* _base;
    private readonly object _bloccoImmagini = new();
    private readonly object _bloccoAudio = new();
    private bool _chiusa;

    /// <param name="nome">Nome della memoria; l'evento si chiama come lei più ".Nuovo". Diverso solo nei test.</param>
    public BachecaObs(string nome = NomePredefinito)
    {
        // Se il plugin tiene ancora aperta la memoria di un ricevitore precedente, si riapre quella.
        _memoria = MemoryMappedFile.CreateOrOpen(nome, DimensioneTotale, MemoryMappedFileAccess.ReadWrite);
        _vista = _memoria.CreateViewAccessor(0, DimensioneTotale, MemoryMappedFileAccess.ReadWrite);
        _nuovo = new EventWaitHandle(false, EventResetMode.AutoReset, nome + ".Nuovo");

        byte* puntatore = null;
        _vista.SafeMemoryMappedViewHandle.AcquirePointer(ref puntatore);
        _base = puntatore + _vista.PointerOffset;

        Prepara();
    }

    /// <summary>Generazione attuale (vedi <see cref="NuovaGenerazione"/>).</summary>
    public ulong Generazione => Volatile.Read(ref *(ulong*)(_base + PosGenerazione));

    /// <summary>L'istante attuale nel tempo di OBS (os_gettime_ns di libobs su Windows).</summary>
    public static ulong OraNs() => DaContatore(Stopwatch.GetTimestamp());

    /// <summary>Converte un valore di <see cref="Stopwatch.GetTimestamp"/> nel tempo di OBS.</summary>
    public static ulong DaContatore(long contatore) =>
        // Stesso calcolo di libobs (util_mul_div64), a 128 bit per non traboccare.
        (ulong)((UInt128)(ulong)contatore * 1_000_000_000UL / (ulong)Stopwatch.Frequency);

    /// <summary>
    /// Nuova trasmissione dall'iPhone: il lettore deve ripartire pulito (istanti non confrontabili
    /// con quelli di prima).
    /// </summary>
    public void NuovaGenerazione()
    {
        lock (_bloccoImmagini)
        {
            if (_chiusa)
                return;
            AumentaGenerazione();
        }
    }

    /// <summary>"Sono ancora qui": va chiamato almeno ogni 100 ms anche senza immagini né audio.</summary>
    public void Battito()
    {
        lock (_bloccoImmagini)
        {
            if (!_chiusa)
                ScriviBattito();
        }
    }

    /// <summary>Scrive un'immagine NV12 già dritta.</summary>
    /// <param name="nv12">Piano Y (altezza righe) seguito dal piano UV (altezza / 2 righe).</param>
    /// <param name="passo">Byte per riga di entrambi i piani in <paramref name="nv12"/> (almeno la larghezza).</param>
    /// <param name="istanteNs">Istante di cattura nel tempo di OBS.</param>
    public void ScriviImmagine(ReadOnlySpan<byte> nv12, int larghezza, int altezza, int passo, ulong istanteNs)
    {
        if (larghezza <= 0 || altezza <= 0 || larghezza % 2 != 0 || altezza % 2 != 0)
            throw new ArgumentException($"Misure NV12 non valide: {larghezza}×{altezza} (servono positive e pari).");
        if (passo < larghezza)
            throw new ArgumentException($"Passo {passo} minore della larghezza {larghezza}.", nameof(passo));
        int byteDati = larghezza * altezza * 3 / 2;
        if (byteDati > DimensioneCassetto - IntestazioneElemento)
            throw new ArgumentException($"Immagine {larghezza}×{altezza} troppo grande per un cassetto.");
        if (nv12.Length < passo * altezza * 3 / 2)
            throw new ArgumentException("Dati NV12 troppo corti.", nameof(nv12));

        lock (_bloccoImmagini)
        {
            ControllaAperta();
            ulong* scritte = (ulong*)(_base + PosImmaginiScritte);
            ulong numero = Volatile.Read(ref *scritte) + 1;
            byte* cassetto = _base + DimensioneIntestazione + (long)((numero - 1) % NumeroCassetti) * DimensioneCassetto;

            IniziaScrittura(cassetto);
            Scrivi64(cassetto + PosNumero, numero);
            Scrivi64(cassetto + PosIstante, istanteNs);
            Scrivi64(cassetto + PosGenerazioneElemento, Generazione);
            Scrivi32(cassetto + PosLarghezza, (uint)larghezza);
            Scrivi32(cassetto + PosAltezza, (uint)altezza);
            // Nella bacheca le righe sono compatte: passo = larghezza per entrambi i piani.
            Scrivi32(cassetto + PosPassoY, (uint)larghezza);
            Scrivi32(cassetto + PosPassoUv, (uint)larghezza);
            Scrivi32(cassetto + PosFormatoImmagine, FormatoNv12);
            Scrivi32(cassetto + PosColore, ColoreBt709Limitato);
            Scrivi32(cassetto + PosByteImmagine, (uint)byteDati);

            var destinazione = new Span<byte>(cassetto + IntestazioneElemento, byteDati);
            int righe = altezza * 3 / 2; // righe Y più righe UV, stesso passo
            for (int riga = 0; riga < righe; riga++)
                nv12.Slice(riga * passo, larghezza).CopyTo(destinazione.Slice(riga * larghezza, larghezza));

            FinisciScrittura(cassetto);
            Volatile.Write(ref *scritte, numero);
            ScriviBattito();
            _nuovo.Set();
        }
    }

    /// <summary>Scrive audio PCM 16 bit interlacciato, dividendolo in più blocchi se serve.</summary>
    /// <param name="istanteNs">Istante del primo campione nel tempo di OBS.</param>
    public void ScriviAudio(ReadOnlySpan<byte> pcm16, int canali, int frequenza, ulong istanteNs)
    {
        if (canali <= 0 || frequenza <= 0)
            throw new ArgumentException($"Formato audio non valido: {canali} canali a {frequenza} Hz.");
        int byteCampione = canali * 2;
        if (pcm16.Length % byteCampione != 0)
            throw new ArgumentException("L'audio non contiene un numero intero di campioni.", nameof(pcm16));
        int campioniPerBlocco = (DimensioneBloccoAudio - IntestazioneElemento) / byteCampione;
        if (campioniPerBlocco == 0)
            throw new ArgumentException($"Troppi canali ({canali}) per un blocco audio.", nameof(canali));

        lock (_bloccoAudio)
        {
            ControllaAperta();
            ulong* scritti = (ulong*)(_base + PosBlocchiScritti);
            int campioniFatti = 0;
            int campioniTotali = pcm16.Length / byteCampione;

            while (campioniFatti < campioniTotali)
            {
                int campioni = Math.Min(campioniPerBlocco, campioniTotali - campioniFatti);
                ulong numero = Volatile.Read(ref *scritti);
                byte* blocco = _base + DimensioneIntestazione + (long)NumeroCassetti * DimensioneCassetto
                    + (long)(numero % NumeroBlocchiAudio) * DimensioneBloccoAudio;
                ulong istante = istanteNs + (ulong)((long)campioniFatti * 1_000_000_000L / frequenza);

                IniziaScrittura(blocco);
                Scrivi64(blocco + PosNumero, numero);
                Scrivi64(blocco + PosIstante, istante);
                Scrivi64(blocco + PosGenerazioneElemento, Generazione);
                Scrivi32(blocco + PosCampioni, (uint)campioni);
                Scrivi32(blocco + PosCanali, (uint)canali);
                Scrivi32(blocco + PosFrequenza, (uint)frequenza);
                Scrivi32(blocco + PosFormatoAudio, FormatoPcm16);
                Scrivi32(blocco + PosByteAudio, (uint)(campioni * byteCampione));
                pcm16.Slice(campioniFatti * byteCampione, campioni * byteCampione)
                    .CopyTo(new Span<byte>(blocco + IntestazioneElemento, campioni * byteCampione));
                FinisciScrittura(blocco);

                Volatile.Write(ref *scritti, numero + 1);
                campioniFatti += campioni;
            }
            ScriviBattito();
            _nuovo.Set();
        }
    }

    public void Dispose()
    {
        lock (_bloccoImmagini)
        lock (_bloccoAudio)
        {
            if (_chiusa)
                return;
            _chiusa = true;
            _vista.SafeMemoryMappedViewHandle.ReleasePointer();
            _vista.Dispose();
            _memoria.Dispose();
            _nuovo.Dispose();
        }
    }

    private void Prepara()
    {
        bool valida = new ReadOnlySpan<byte>(_base + PosFirma, 4).SequenceEqual(Firma)
            && Leggi32(_base + PosVersione) == Versione
            && Leggi64(_base + PosDimensione) == (ulong)DimensioneTotale
            && Leggi32(_base + PosNumeroCassetti) == NumeroCassetti
            && Leggi32(_base + PosDimensioneCassetto) == DimensioneCassetto
            && Leggi32(_base + PosNumeroBlocchi) == NumeroBlocchiAudio
            && Leggi32(_base + PosDimensioneBlocco) == DimensioneBloccoAudio;

        if (!valida)
        {
            // Memoria nuova (o di un formato diverso): si parte da zero, firma per ultima, così un
            // lettore non riconosce come valida una bacheca a metà.
            new Span<byte>(_base, DimensioneIntestazione).Clear();
            Scrivi64(_base + PosDimensione, (ulong)DimensioneTotale);
            Scrivi32(_base + PosNumeroCassetti, NumeroCassetti);
            Scrivi32(_base + PosDimensioneCassetto, DimensioneCassetto);
            Scrivi32(_base + PosNumeroBlocchi, NumeroBlocchiAudio);
            Scrivi32(_base + PosDimensioneBlocco, DimensioneBloccoAudio);
            Scrivi32(_base + PosVersione, Versione);
            Interlocked.MemoryBarrier();
            Firma.CopyTo(new Span<byte>(_base + PosFirma, 4));
        }

        // Riaperta o nuova, per il lettore è comunque una trasmissione diversa da prima. I contatori
        // invece restano: se tornassero indietro il lettore li prenderebbe per vecchi.
        Scrivi32(_base + PosProcesso, (uint)Environment.ProcessId);
        AumentaGenerazione();
    }

    private void AumentaGenerazione()
    {
        Interlocked.Increment(ref *(ulong*)(_base + PosGenerazione));
        ScriviBattito();
    }

    private void ScriviBattito() => Volatile.Write(ref *(ulong*)(_base + PosBattito), OraNs());

    private void ControllaAperta() => ObjectDisposedException.ThrowIf(_chiusa, this);

    // Seqlock: sequenza dispari mentre si scrive, pari a scrittura finita (vedi il documento).
    private static void IniziaScrittura(byte* elemento)
    {
        ulong* sequenza = (ulong*)(elemento + PosSequenza);
        Volatile.Write(ref *sequenza, *sequenza + 1);
        Interlocked.MemoryBarrier();
    }

    private static void FinisciScrittura(byte* elemento)
    {
        Interlocked.MemoryBarrier();
        ulong* sequenza = (ulong*)(elemento + PosSequenza);
        Volatile.Write(ref *sequenza, *sequenza + 1);
    }

    private static void Scrivi32(byte* dove, uint valore) =>
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(dove, 4), valore);

    private static void Scrivi64(byte* dove, ulong valore) =>
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(dove, 8), valore);

    private static uint Leggi32(byte* dove) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(dove, 4));

    private static ulong Leggi64(byte* dove) => BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(dove, 8));
}
