using System.Runtime.InteropServices;

namespace MirrorLoop.Ricevitore;

/// <summary>Il tipo di rete scelto in Windows (Impostazioni → Rete e Internet → Tipo di profilo di rete).</summary>
public enum CategoriaDiRete
{
    Pubblica = 0,
    Privata = 1,
    Dominio = 2,
}

/// <summary>
/// Il tipo della rete di casa, letto con il Network List Manager di Windows (netlistmgr.h, la stessa
/// API che usa Windows per mostrarlo). Serve per l'avviso: il firewall lascia passare l'iPhone solo
/// sulle reti private, e Windows segna spesso come pubblica una rete nuova.
/// </summary>
public static class CategoriaRete
{
    /// <summary>La categoria della rete della scheda di casa; null se non si riesce a saperla.</summary>
    public static CategoriaDiRete? DellaReteDiCasa() =>
        AnnuncioBonjour.InterfacciaDiCasa() is { } scheda ? DellaScheda(scheda.Id) : null;

    /// <summary>
    /// La categoria della rete a cui è collegata la scheda con questo Id (NetworkInterface.Id, un
    /// GUID). Null se la scheda non ha una connessione nota a Windows o se la lettura non riesce:
    /// meglio nessun avviso che un avviso sbagliato.
    /// </summary>
    public static CategoriaDiRete? DellaScheda(string idScheda)
    {
        if (!Guid.TryParse(idScheda, out Guid scheda))
            return null;
        try
        {
            var gestore = (INetworkListManager)new NetworkListManager();
            if (gestore.GetNetworkConnections(out IEnumNetworkConnections? elenco) != 0 || elenco is null)
                return null;
            var una = new INetworkConnection?[1];
            while (elenco.Next(1, una, out uint lette) == 0 && lette == 1)
            {
                INetworkConnection connessione = una[0]!;
                if (connessione.GetAdapterId(out Guid idTrovato) == 0 && idTrovato == scheda
                    && connessione.GetNetwork(out INetwork? rete) == 0 && rete is not null
                    && rete.GetCategory(out int categoria) == 0
                    && Enum.IsDefined((CategoriaDiRete)categoria))
                    return (CategoriaDiRete)categoria;
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Il testo da mostrare nella finestra, o null se non serve.</summary>
    public static string? Avviso(CategoriaDiRete? categoria) =>
        categoria == CategoriaDiRete.Pubblica
            ? "⚠ Questa rete è impostata come Pubblica: l'iPhone non potrà collegarsi.\n" +
              "Impostala come Privata: Impostazioni → Rete e Internet → (la tua rete) → Tipo di profilo di rete."
            : null;

    // ---- Windows (netlistmgr.h, Windows SDK 10.0.26100) ----
    // Interfacce "dual" (derivano da IDispatch): i metodi vanno elencati nell'ordine dell'header,
    // fino all'ultimo che serve.

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager { }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [PreserveSig] int GetNetworks(int flags, [MarshalAs(UnmanagedType.Interface)] out object? elenco);
        [PreserveSig] int GetNetwork(Guid idRete, out INetwork? rete);
        [PreserveSig] int GetNetworkConnections(out IEnumNetworkConnections? elenco);
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [PreserveSig] int get__NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? enumeratore);
        [PreserveSig] int Next(uint quante, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection?[] connessioni, out uint lette);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [PreserveSig] int GetNetwork(out INetwork? rete);
        [PreserveSig] int get_IsConnectedToInternet(out short connessa);
        [PreserveSig] int get_IsConnected(out short connessa);
        [PreserveSig] int GetConnectivity(out int connettivita);
        [PreserveSig] int GetConnectionId(out Guid idConnessione);
        [PreserveSig] int GetAdapterId(out Guid idScheda);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string? nome);
        [PreserveSig] int SetName([MarshalAs(UnmanagedType.BStr)] string nome);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string? descrizione);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.BStr)] string descrizione);
        [PreserveSig] int GetNetworkId(out Guid idRete);
        [PreserveSig] int GetDomainType(out int tipo);
        [PreserveSig] int GetNetworkConnections([MarshalAs(UnmanagedType.Interface)] out object? elenco);
        [PreserveSig] int GetTimeCreatedAndConnected(out uint creataBassa, out uint creataAlta, out uint collegataBassa, out uint collegataAlta);
        [PreserveSig] int get_IsConnectedToInternet(out short connessa);
        [PreserveSig] int get_IsConnected(out short connessa);
        [PreserveSig] int GetConnectivity(out int connettivita);
        [PreserveSig] int GetCategory(out int categoria);
    }
}
