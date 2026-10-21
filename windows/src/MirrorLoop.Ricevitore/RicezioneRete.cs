using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MirrorLoop.Protocollo;
using Ping = MirrorLoop.Protocollo.Ping;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Ascolta il mittente e legge i frame dalla rete in continuazione, mettendoli nella coda.
/// Non fa altro: leggere subito è ciò che impedisce al ritardo di accumularsi nei buffer di Windows.
/// </summary>
public sealed class RicezioneRete(
    int porta,
    CodaFrame coda,
    StatisticheRicezione statistiche,
    StatisticheAudio statisticheAudio,
    RiproduzioneAudio riproduzioneAudio,
    SincroniaOrologi sincronia)
{
    // Un ping al secondo basta: lo scarto tra gli orologi cambia molto lentamente.
    private static readonly TimeSpan IntervalloPing = TimeSpan.FromSeconds(1);

    /// <summary>Descrizione dello stato, per la finestra. Viene chiamato da un thread in sottofondo.</summary>
    public event Action<string>? StatoCambiato;

    public async Task EseguiAsync(CancellationToken stop)
    {
        // In ascolto su tutte le reti del PC, così l'iPhone lo raggiunge dalla rete di casa.
        // Al primo avvio Windows chiede il permesso del firewall: va consentito solo sulle reti private.
        var ascolto = new TcpListener(IPAddress.Any, porta);
        ascolto.Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // Riletti a ogni attesa: il PC potrebbe aver cambiato rete nel frattempo.
                string indirizzi = string.Join("  ·  ", IndirizziLocali().Select(i => $"{i}:{porta}"));
                StatoCambiato?.Invoke($"In attesa dell'iPhone su {indirizzi}");
                using TcpClient client = await ascolto.AcceptTcpClientAsync(stop);
                client.NoDelay = true;
                StatoCambiato?.Invoke($"Collegato a {client.Client.RemoteEndPoint}");

                coda.Ricomincia();
                riproduzioneAudio.Ricomincia();
                statisticheAudio.Ricomincia();
                sincronia.Ricomincia();
                using var fineConnessione = CancellationTokenSource.CreateLinkedTokenSource(stop);
                NetworkStream flusso = client.GetStream();
                Task ping = MandaPingAsync(flusso, fineConnessione.Token);
                try
                {
                    await RiceviTuttoAsync(flusso, stop);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
                {
                    // Il mittente è sparito o ha mandato dati non validi: si torna ad aspettare.
                    StatoCambiato?.Invoke($"Connessione persa: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), stop);
                }
                finally
                {
                    await fineConnessione.CancelAsync();
                    await ping;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Chiusura della finestra.
        }
        finally
        {
            ascolto.Stop();
        }
    }

    /// <summary>
    /// Gli indirizzi IPv4 con cui il PC è raggiungibile nella rete locale, da scrivere nell'app.
    /// Se non ce n'è nessuno (PC senza rete) resta 127.0.0.1, utile per le prove con MittenteTest.
    /// </summary>
    public static IReadOnlyList<IPAddress> IndirizziLocali()
    {
        var indirizzi = NetworkInterface.GetAllNetworkInterfaces()
            .Where(s => s.OperationalStatus == OperationalStatus.Up && s.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(s => s.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            // 169.254.x.x è l'indirizzo che Windows si assegna da solo quando manca la rete: inutile.
            .Where(a => a.GetAddressBytes()[0] != 169 || a.GetAddressBytes()[1] != 254)
            .Distinct()
            .ToList();
        return indirizzi.Count > 0 ? indirizzi : [IPAddress.Loopback];
    }

    /// <summary>
    /// Millisecondi passati dalla cattura sull'iPhone a adesso. Con gli orologi sincronizzati è la
    /// latenza vera; senza, il confronto diretto tra i due orologi (può venire anche negativo).
    /// </summary>
    public long LatenzaMicrosecondi(long istanteCatturaIphone) =>
        Protocollo.Protocollo.AdessoMicrosecondi() - (sincronia.InOraDelPc(istanteCatturaIphone) ?? istanteCatturaIphone);

    private double LatenzaMs(long istanteCatturaIphone) => LatenzaMicrosecondi(istanteCatturaIphone) / 1000.0;

    /// <summary>
    /// Manda un ping al secondo finché dura la connessione. Lettura (l'altro task) e scrittura
    /// sullo stesso NetworkStream da due task diversi sono consentite.
    /// </summary>
    private static async Task MandaPingAsync(NetworkStream flusso, CancellationToken stop)
    {
        int numero = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Protocollo.Protocollo.InviaPingAsync(flusso, new Ping(++numero, Protocollo.Protocollo.AdessoMicrosecondi()), stop);
                await Task.Delay(IntervalloPing, stop);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Fine della connessione: se ne accorge anche la lettura.
        }
    }

    private async Task RiceviTuttoAsync(NetworkStream flusso, CancellationToken stop)
    {
        while (await Protocollo.Protocollo.RiceviMessaggioAsync(flusso, stop) is Messaggio messaggio)
        {
            switch (messaggio)
            {
                case FrameVideo frame:
                    double latenzaMs = LatenzaMs(frame.IstanteCatturaMicrosecondi);
                    statistiche.FrameRicevuto(frame.Keyframe, frame.DatiH264.Length, latenzaMs);
                    coda.Aggiungi(frame);
                    break;
                case PacchettoAudio audio:
                    statisticheAudio.PacchettoRicevuto(audio);
                    riproduzioneAudio.Aggiungi(audio);
                    break;
                case Pong pong:
                    sincronia.Registra(pong, Protocollo.Protocollo.AdessoMicrosecondi());
                    break;
            }
        }
    }
}
