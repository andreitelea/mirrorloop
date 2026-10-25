using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MirrorLoop.Ricevitore.Test;

/// <summary>
/// L'annuncio deve essere davvero visibile in rete: lo si cerca con la ricerca DNS-SD di Windows
/// (DnsServiceBrowse), come farà l'iPhone con la sua.
/// </summary>
public sealed class AnnuncioBonjourTest
{
    [Fact]
    public async Task LAnnuncioSiTrovaCercandoIlTipoDiServizio()
    {
        string nome = "MirrorLoopTest" + Guid.NewGuid().ToString("N")[..8];
        using var annuncio = new AnnuncioBonjour(nome, 7451);

        uint esito = await annuncio.Registrazione.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0u, esito);
        Assert.StartsWith("visibile in rete come " + nome, annuncio.Stato);

        using var ricerca = new RicercaBonjour($"{AnnuncioBonjour.TipoServizio}.local", nome);
        await Task.WhenAny(ricerca.Trovato, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Contains(ricerca.Istanze, i => i.StartsWith(nome + ".", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Ricerca di un tipo di servizio con l'API di Windows (windns.h; DNS_RECORDW da windnsdef.h).</summary>
internal sealed unsafe partial class RicercaBonjour : IDisposable
{
    private const uint DnsRequestPending = 9506;
    private const ushort DnsTypePtr = 12;
    // DNS_RECORDW su 64 bit: pNext 8 + pName 8 + wType 2 + wDataLength 2 + Flags 4 + dwTtl 4 + dwReserved 4.
    private const int PosizioneTipo = 16;
    private const int PosizioneDati = 32;

    private readonly string _cercato;
    private readonly TaskCompletionSource _trovato = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentBag<string> _istanze = [];
    private readonly GCHandle _contesto;
    private readonly nint _nomeTipo;
    private readonly DnsServiceBrowseRequest* _richiesta;
    private readonly nint* _annulla;

    public RicercaBonjour(string tipo, string cercato)
    {
        _cercato = cercato;
        _contesto = GCHandle.Alloc(this);
        _nomeTipo = Marshal.StringToHGlobalUni(tipo);
        _richiesta = (DnsServiceBrowseRequest*)NativeMemory.AllocZeroed((nuint)sizeof(DnsServiceBrowseRequest));
        _annulla = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        _richiesta->Version = 1;
        _richiesta->QueryName = _nomeTipo;
        _richiesta->pBrowseCallback = &Trovati;
        _richiesta->pQueryContext = GCHandle.ToIntPtr(_contesto);
        Assert.Equal(DnsRequestPending, DnsServiceBrowse(_richiesta, _annulla));
    }

    /// <summary>I nomi delle istanze trovate, per esempio "Nome._mirrorloop._udp.local".</summary>
    public IReadOnlyCollection<string> Istanze => _istanze;

    /// <summary>Completato quando compare l'istanza cercata.</summary>
    public Task Trovato => _trovato.Task;

    public void Dispose()
    {
        DnsServiceBrowseCancel(_annulla);
        // Un callback può ancora arrivare subito dopo l'annullamento: si lascia finire prima di liberare.
        Thread.Sleep(200);
        NativeMemory.Free(_richiesta);
        NativeMemory.Free(_annulla);
        Marshal.FreeHGlobal(_nomeTipo);
        _contesto.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Trovati(uint esito, nint contesto, nint record)
    {
        try
        {
            if (GCHandle.FromIntPtr(contesto).Target is not RicercaBonjour ricerca)
                return;
            for (nint r = record; r != 0; r = *(nint*)r)
            {
                if (*(ushort*)(r + PosizioneTipo) != DnsTypePtr)
                    continue;
                if (Marshal.PtrToStringUni(*(nint*)(r + PosizioneDati)) is not string istanza)
                    continue;
                ricerca._istanze.Add(istanza);
                if (istanza.StartsWith(ricerca._cercato + ".", StringComparison.OrdinalIgnoreCase))
                    ricerca._trovato.TrySetResult();
            }
        }
        finally
        {
            if (record != 0)
                DnsRecordListFree(record, 1); // DnsFreeRecordList
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceBrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint QueryName;
        public delegate* unmanaged[Stdcall]<uint, nint, nint, void> pBrowseCallback;
        public nint pQueryContext;
    }

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceBrowse(DnsServiceBrowseRequest* pRequest, nint* pCancel);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceBrowseCancel(nint* pCancelHandle);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsRecordListFree(nint pRecordList, int freeType);
}
