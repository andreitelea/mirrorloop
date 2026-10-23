using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace MirrorLoop.Ricevitore.Test;

/// <summary>
/// Lettore della bacheca scritto seguendo solo docs/memoria-condivisa.md, come farà il plugin OBS:
/// se i test passano, il documento basta per leggere quello che scrive <see cref="BachecaObs"/>.
/// </summary>
internal sealed unsafe class LettoreBacheca : IDisposable
{
    public sealed record Immagine(ulong Numero, ulong Istante, ulong Generazione, int Larghezza, int Altezza,
        int PassoY, int PassoUv, uint Formato, uint Colore, byte[] Dati);

    public sealed record BloccoAudio(ulong Numero, ulong Istante, ulong Generazione, int Campioni, int Canali,
        int Frequenza, uint Formato, byte[] Dati);

    private readonly MemoryMappedFile _memoria;
    private readonly MemoryMappedViewAccessor _vista;
    private readonly byte* _base;
    private ulong _prossimoBlocco;

    public LettoreBacheca(string nome)
    {
        _memoria = MemoryMappedFile.OpenExisting(nome, MemoryMappedFileRights.Read);
        _vista = _memoria.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        Nuovo = EventWaitHandle.OpenExisting(nome + ".Nuovo");
        byte* puntatore = null;
        _vista.SafeMemoryMappedViewHandle.AcquirePointer(ref puntatore);
        _base = puntatore + _vista.PointerOffset;
    }

    public EventWaitHandle Nuovo { get; }

    /// <summary>Chiamato a metà della copia di un'immagine: serve ai test per simulare una sovrascrittura.</summary>
    public Action? DuranteLaCopia { get; set; }

    /// <summary>Blocchi audio persi perché sovrascritti prima della lettura.</summary>
    public ulong BlocchiPersi { get; private set; }

    public string Firma => System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(_base, 4));
    public uint Versione => L32(_base + 4);
    public ulong Dimensione => L64(_base + 8);
    public ulong Battito => Volatile.Read(ref *(ulong*)(_base + 16));
    public ulong Generazione => Volatile.Read(ref *(ulong*)(_base + 24));
    public ulong ImmaginiScritte => Volatile.Read(ref *(ulong*)(_base + 32));
    public ulong BlocchiScritti => Volatile.Read(ref *(ulong*)(_base + 40));
    public uint NumeroCassetti => L32(_base + 48);
    public uint DimensioneCassetto => L32(_base + 52);
    public uint NumeroBlocchi => L32(_base + 56);
    public uint DimensioneBlocco => L32(_base + 60);
    public uint Processo => L32(_base + 64);

    /// <summary>L'ultima immagine completa, o null se non c'è o se la copia è risultata mista.</summary>
    public Immagine? UltimaImmagine()
    {
        ulong numero = ImmaginiScritte;
        if (numero == 0)
            return null;
        byte* cassetto = _base + 4096 + (long)((numero - 1) % NumeroCassetti) * DimensioneCassetto;

        ulong sequenza = Volatile.Read(ref *(ulong*)cassetto);
        if (sequenza % 2 != 0 || L64(cassetto + 8) != numero)
            return null;

        int byteDati = (int)L32(cassetto + 56);
        var dati = new byte[byteDati];
        new ReadOnlySpan<byte>(cassetto + 64, byteDati / 2).CopyTo(dati);
        DuranteLaCopia?.Invoke();
        new ReadOnlySpan<byte>(cassetto + 64 + byteDati / 2, byteDati - byteDati / 2).CopyTo(dati.AsSpan(byteDati / 2));
        var immagine = new Immagine(numero, L64(cassetto + 16), L64(cassetto + 24), (int)L32(cassetto + 32),
            (int)L32(cassetto + 36), (int)L32(cassetto + 40), (int)L32(cassetto + 44), L32(cassetto + 48),
            L32(cassetto + 52), dati);

        Interlocked.MemoryBarrier();
        return Volatile.Read(ref *(ulong*)cassetto) == sequenza ? immagine : null;
    }

    /// <summary>Tutti i blocchi audio nuovi dall'ultima chiamata, in ordine.</summary>
    public List<BloccoAudio> NuoviBlocchi()
    {
        var blocchi = new List<BloccoAudio>();
        ulong scritti = BlocchiScritti;
        if (scritti - _prossimoBlocco > NumeroBlocchi)
        {
            BlocchiPersi += scritti - NumeroBlocchi - _prossimoBlocco;
            _prossimoBlocco = scritti - NumeroBlocchi;
        }

        for (; _prossimoBlocco < scritti; _prossimoBlocco++)
        {
            byte* blocco = _base + 4096 + (long)NumeroCassetti * DimensioneCassetto
                + (long)(_prossimoBlocco % NumeroBlocchi) * DimensioneBlocco;
            ulong sequenza = Volatile.Read(ref *(ulong*)blocco);
            if (sequenza % 2 != 0 || L64(blocco + 8) != _prossimoBlocco)
            {
                BlocchiPersi++;
                continue;
            }
            int byteDati = (int)L32(blocco + 48);
            var letto = new BloccoAudio(_prossimoBlocco, L64(blocco + 16), L64(blocco + 24), (int)L32(blocco + 32),
                (int)L32(blocco + 36), (int)L32(blocco + 40), L32(blocco + 44),
                new ReadOnlySpan<byte>(blocco + 64, byteDati).ToArray());
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref *(ulong*)blocco) == sequenza)
                blocchi.Add(letto);
            else
                BlocchiPersi++;
        }
        return blocchi;
    }

    public void Dispose()
    {
        _vista.SafeMemoryMappedViewHandle.ReleasePointer();
        _vista.Dispose();
        _memoria.Dispose();
        Nuovo.Dispose();
    }

    private static uint L32(byte* dove) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(dove, 4));

    private static ulong L64(byte* dove) => BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(dove, 8));
}
