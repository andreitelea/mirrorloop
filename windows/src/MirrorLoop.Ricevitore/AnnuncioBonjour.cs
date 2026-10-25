using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Annuncia il ricevitore nella rete di casa con mDNS/DNS-SD (lo stesso meccanismo di Bonjour),
/// così l'iPhone lo trova senza scrivere l'indirizzo. Usa l'API di sistema di Windows 10 e 11
/// (DnsServiceRegister in dnsapi.dll, firme prese da windns.h del Windows SDK): niente da
/// installare. Se il processo termina, Windows toglie l'annuncio da solo.
///
/// Si annuncia solo sulla scheda di rete che ha un gateway (quella di casa) e con il suo indirizzo
/// IPv4: le schede virtuali, come quella di Hyper-V, non hanno gateway e darebbero all'iPhone un
/// indirizzo che non raggiunge.
/// </summary>
public sealed unsafe partial class AnnuncioBonjour : IDisposable
{
    /// <summary>
    /// Tipo di servizio DNS-SD: deve coincidere con NSBonjourServices dell'app. "_udp" perché
    /// QUIC viaggia su UDP (RFC 6763: "_tcp" solo per i servizi su TCP); fino alla versione 1 era "_tcp".
    /// </summary>
    public const string TipoServizio = "_mirrorloop._udp";

    private const uint DnsQueryRequestVersion1 = 1;
    private const uint DnsRequestPending = 9506;
    private static readonly TimeSpan AttesaDeregistrazione = TimeSpan.FromSeconds(2);

    private readonly object _blocco = new();
    private readonly DnsServiceRegisterRequest* _richiesta;
    private readonly nint _istanza;
    private GCHandle _contesto;
    private TaskCompletionSource<uint> _completato = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _registrato;
    private bool _chiuso;

    /// <param name="nome">Il nome che vedrà l'iPhone (di solito il nome del PC). Senza punti.</param>
    public AnnuncioBonjour(string nome, ushort porta)
    {
        Nome = nome;
        var interfaccia = InterfacciaDiCasa();
        Indirizzo = interfaccia?.Indirizzo;

        uint ip4 = 0;
        if (interfaccia is { } scelta)
            ip4 = BitConverter.ToUInt32(scelta.Indirizzo.GetAddressBytes()); // in memoria nell'ordine di rete, come IP4_ADDRESS

        // Una proprietà nel record TXT: la versione del protocollo, per riconoscere ricevitori futuri incompatibili.
        nint chiave = Marshal.StringToHGlobalUni("versione");
        nint valore = Marshal.StringToHGlobalUni("2");
        try
        {
            _istanza = DnsServiceConstructInstance(
                $"{nome}.{TipoServizio}.local", $"{Environment.MachineName}.local",
                interfaccia is null ? null : &ip4, null, porta, 0, 0, 1, &chiave, &valore);
        }
        finally
        {
            Marshal.FreeHGlobal(chiave);
            Marshal.FreeHGlobal(valore);
        }

        // La richiesta deve restare allo stesso indirizzo finché Windows la usa (anche per togliere l'annuncio).
        _richiesta = (DnsServiceRegisterRequest*)NativeMemory.AllocZeroed((nuint)sizeof(DnsServiceRegisterRequest));
        _contesto = GCHandle.Alloc(this);
        _richiesta->Version = DnsQueryRequestVersion1;
        _richiesta->InterfaceIndex = interfaccia?.Indice ?? 0;
        _richiesta->pServiceInstance = _istanza;
        _richiesta->pRegisterCompletionCallback = &Completato;
        _richiesta->pQueryContext = GCHandle.ToIntPtr(_contesto);
        _richiesta->unicastEnabled = 0; // mDNS, non un server DNS

        if (_istanza == 0)
        {
            Stato = "impossibile preparare l'annuncio";
            return;
        }
        uint esito = DnsServiceRegister(_richiesta, null);
        if (esito != DnsRequestPending)
        {
            // Rifiutato subito: Windows non chiamerà mai il callback.
            Stato = $"annuncio rifiutato da Windows (errore {esito})";
            _completato.TrySetResult(esito);
        }
    }

    public string Nome { get; }

    /// <summary>L'indirizzo annunciato, o null se non c'è una scheda con gateway (allora si lascia scegliere a Windows).</summary>
    public IPAddress? Indirizzo { get; }

    /// <summary>Descrizione per la finestra.</summary>
    public string Stato { get; private set; } = "annuncio in corso…";

    /// <summary>Completato con il codice di Windows (0 = riuscito) quando la registrazione finisce.</summary>
    public Task<uint> Registrazione => _completato.Task;

    public void Dispose()
    {
        lock (_blocco)
        {
            if (_chiuso)
                return;
            _chiuso = true;
        }

        bool liberabile = true;
        if (_registrato)
        {
            // Togliere l'annuncio è asincrono come metterlo: si aspetta la conferma prima di liberare
            // la memoria che Windows sta ancora usando.
            _completato = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (DnsServiceDeRegister(_richiesta, null) == DnsRequestPending)
                liberabile = _completato.Task.Wait(AttesaDeregistrazione);
        }
        else if (_istanza != 0 && !_completato.Task.IsCompleted)
        {
            // Registrazione mai confermata: Windows potrebbe ancora chiamarci.
            liberabile = false;
        }

        // Senza conferma si lascia tutto com'è: pochi byte persi alla chiusura, piuttosto che
        // un callback di Windows su memoria già liberata.
        if (!liberabile)
            return;
        if (_istanza != 0)
            DnsServiceFreeInstance(_istanza);
        NativeMemory.Free(_richiesta);
        _contesto.Free();
    }

    private void SuCompletato(uint esito)
    {
        lock (_blocco)
        {
            if (!_chiuso)
            {
                _registrato = esito == 0;
                Stato = esito == 0
                    ? $"visibile in rete come {Nome}{(Indirizzo is null ? "" : $" ({Indirizzo})")}"
                    : $"annuncio non riuscito (errore {esito})";
            }
        }
        _completato.TrySetResult(esito);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Completato(uint esito, nint contesto, nint istanza)
    {
        // La copia del servizio che Windows ci passa va liberata da noi (documentazione del callback).
        if (istanza != 0)
            DnsServiceFreeInstance(istanza);
        if (GCHandle.FromIntPtr(contesto).Target is AnnuncioBonjour annuncio)
            annuncio.SuCompletato(esito);
    }

    /// <summary>La scheda attiva con un gateway IPv4, cioè quella che porta alla rete di casa.</summary>
    private static (uint Indice, IPAddress Indirizzo)? InterfacciaDiCasa()
    {
        foreach (NetworkInterface scheda in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (scheda.OperationalStatus != OperationalStatus.Up || scheda.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            IPInterfaceProperties proprieta = scheda.GetIPProperties();
            bool conGateway = proprieta.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            IPAddress? ipv4 = proprieta.UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (conGateway && ipv4 is not null)
                return ((uint)proprieta.GetIPv4Properties().Index, ipv4);
        }
        return null;
    }

    // ---- Windows (windns.h) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint pServiceInstance;
        public delegate* unmanaged[Stdcall]<uint, nint, nint, void> pRegisterCompletionCallback;
        public nint pQueryContext;
        public nint hCredentials;
        public int unicastEnabled;
    }

    [LibraryImport("dnsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint DnsServiceConstructInstance(string pServiceName, string pHostName, uint* pIp4, void* pIp6,
        ushort wPort, ushort wPriority, ushort wWeight, uint dwPropertiesCount, nint* keys, nint* values);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsServiceFreeInstance(nint pInstance);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceRegister(DnsServiceRegisterRequest* pRequest, void* pCancel);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceDeRegister(DnsServiceRegisterRequest* pRequest, void* pCancel);
}
