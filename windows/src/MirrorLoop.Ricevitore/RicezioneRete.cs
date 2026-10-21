using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MirrorLoop.Protocollo;
using Ping = MirrorLoop.Protocollo.Ping;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Ascolta il mittente e legge i messaggi dalla rete in continuazione. Leggere subito è ciò che
/// impedisce al ritardo di accumularsi nei buffer di Windows.
///
/// Due ingressi sulla stessa porta: TCP (una sola fila per tutto) e QUIC su UDP, dove l'iPhone
/// apre corsie indipendenti (controllo, video, audio) così una perdita nel video non blocca l'audio.
/// Un solo mittente alla volta: una nuova connessione, di qualunque tipo, chiude la precedente.
/// </summary>
public sealed class RicezioneRete(
    int porta,
    CodaFrame coda,
    StatisticheRicezione statistiche,
    StatisticheAudio statisticheAudio,
    RiproduzioneAudio riproduzioneAudio,
    SincroniaOrologi sincronia,
    RegistroPacchetti registro,
    X509Certificate2? certificato)
{
    /// <summary>Nome del protocollo applicativo negoziato in QUIC (ALPN): l'iPhone deve usare lo stesso.</summary>
    public static readonly SslApplicationProtocol ProtocolloQuic = new("mirrorloop/1");

    // Un ping al secondo basta: lo scarto tra gli orologi cambia molto lentamente.
    private static readonly TimeSpan IntervalloPing = TimeSpan.FromSeconds(1);

    private readonly object _bloccoSessione = new();
    private CancellationTokenSource? _sessione;

    /// <summary>Descrizione dello stato, per la finestra. Viene chiamato da un thread in sottofondo.</summary>
    public event Action<string>? StatoCambiato;

    /// <summary>"TCP", "QUIC" o null se nessuno è collegato.</summary>
    public string? Trasporto { get; private set; }

    /// <summary>Se QUIC non è disponibile, il motivo.</summary>
    public string? MotivoSenzaQuic { get; private set; }

    public async Task EseguiAsync(CancellationToken stop)
    {
        Task quic = Task.CompletedTask;
        if (!QuicListener.IsSupported)
            MotivoSenzaQuic = "QUIC non supportato da questo Windows (serve Windows 11)";
        else if (certificato is null)
            MotivoSenzaQuic = "certificato non disponibile";
        else
            quic = AscoltaQuicAsync(certificato, stop);

        AnnunciaAttesa(null);
        await Task.WhenAll(AscoltaTcpAsync(stop), quic);
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

    // ---- TCP ----

    private async Task AscoltaTcpAsync(CancellationToken stop)
    {
        // In ascolto su tutte le reti del PC, così l'iPhone lo raggiunge dalla rete di casa.
        // Al primo avvio Windows chiede il permesso del firewall: va consentito solo sulle reti private.
        var ascolto = new TcpListener(IPAddress.Any, porta);
        ascolto.Start();
        try
        {
            while (true)
            {
                TcpClient client = await ascolto.AcceptTcpClientAsync(stop);
                client.NoDelay = true;
                _ = GestisciTcpAsync(client, stop);
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

    private async Task GestisciTcpAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            CancellationToken sessione = NuovaSessione(stop, "TCP", client.Client.RemoteEndPoint);
            NetworkStream flusso = client.GetStream();
            using var fine = CancellationTokenSource.CreateLinkedTokenSource(sessione);
            Task ping = MandaPingAsync(flusso, fine.Token);
            string? errore = null;
            try
            {
                await RiceviTuttoAsync(flusso, sessione);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or OperationCanceledException)
            {
                errore = ex is OperationCanceledException ? null : ex.Message;
            }
            finally
            {
                await fine.CancelAsync();
                await ping;
                FineSessione(sessione, errore);
            }
        }
    }

    // ---- QUIC ----

    private async Task AscoltaQuicAsync(X509Certificate2 certificatoServer, CancellationToken stop)
    {
        var opzioniConnessione = new QuicServerConnectionOptions
        {
            // Codici di errore applicativi (obbligatori in QUIC): 0 = chiusura normale.
            DefaultStreamErrorCode = 1,
            DefaultCloseErrorCode = 0,
            // L'iPhone apre una corsia bidirezionale (controllo) e quelle unidirezionali (video, audio).
            MaxInboundBidirectionalStreams = 4,
            MaxInboundUnidirectionalStreams = 16,
            // Senza notizie per qualche secondo il mittente si considera sparito, come con TCP.
            IdleTimeout = TimeSpan.FromSeconds(5),
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [ProtocolloQuic],
                ServerCertificate = certificatoServer,
            },
        };

        QuicListener ascolto;
        try
        {
            ascolto = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, porta),
                ApplicationProtocols = [ProtocolloQuic],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(opzioniConnessione),
            }, stop);
        }
        catch (Exception ex) when (ex is QuicException or SocketException)
        {
            MotivoSenzaQuic = $"impossibile ascoltare in QUIC: {ex.Message}";
            AnnunciaAttesa(null);
            return;
        }

        await using (ascolto)
        {
            while (!stop.IsCancellationRequested)
            {
                QuicConnection connessione;
                try
                {
                    connessione = await ascolto.AcceptConnectionAsync(stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is QuicException or AuthenticationException or OperationCanceledException)
                {
                    // Una connessione fallita (per esempio il TLS) non ferma l'ascolto.
                    continue;
                }
                _ = GestisciQuicAsync(connessione, stop);
            }
        }
    }

    private async Task GestisciQuicAsync(QuicConnection connessione, CancellationToken stop)
    {
        await using (connessione)
        {
            CancellationToken sessione = NuovaSessione(stop, "QUIC", connessione.RemoteEndPoint);
            using var fineCorsie = CancellationTokenSource.CreateLinkedTokenSource(sessione);
            var corsie = new List<Task>();
            string? errore = null;
            try
            {
                while (true)
                {
                    QuicStream corsia = await connessione.AcceptInboundStreamAsync(sessione);
                    corsie.Add(GestisciCorsiaAsync(corsia, fineCorsie.Token));
                }
            }
            catch (QuicException ex)
            {
                // Fine della connessione: chiusa dal mittente, scaduta per inattività o persa.
                errore = ex.QuicError == QuicError.ConnectionAborted && ex.ApplicationErrorCode == 0 ? null : ex.Message;
            }
            catch (OperationCanceledException)
            {
                // Sostituita da una connessione nuova, o chiusura della finestra.
            }
            finally
            {
                await fineCorsie.CancelAsync();
                await Task.WhenAll(corsie);
                FineSessione(sessione, errore);
            }
        }
    }

    private async Task GestisciCorsiaAsync(QuicStream corsia, CancellationToken stop)
    {
        await using (corsia)
        {
            using var fine = CancellationTokenSource.CreateLinkedTokenSource(stop);
            // La corsia bidirezionale è quella di controllo: da lì partono i ping verso l'iPhone.
            Task ping = corsia.Type == QuicStreamType.Bidirectional ? MandaPingAsync(corsia, fine.Token) : Task.CompletedTask;
            try
            {
                await RiceviTuttoAsync(corsia, stop);
            }
            catch (Exception ex) when (ex is QuicException or IOException or EndOfStreamException or InvalidDataException or OperationCanceledException)
            {
                // La corsia si è chiusa insieme alla connessione: lo gestisce GestisciQuicAsync.
            }
            finally
            {
                await fine.CancelAsync();
                await ping;
            }
        }
    }

    // ---- In comune ----

    /// <summary>Una connessione nuova chiude la precedente e riparte con lo stato pulito.</summary>
    private CancellationToken NuovaSessione(CancellationToken stop, string trasporto, EndPoint? remoto)
    {
        var nuova = CancellationTokenSource.CreateLinkedTokenSource(stop);
        CancellationTokenSource? vecchia;
        lock (_bloccoSessione)
        {
            vecchia = _sessione;
            _sessione = nuova;
            Trasporto = trasporto;
        }
        vecchia?.Cancel();
        coda.Ricomincia();
        riproduzioneAudio.Ricomincia();
        statisticheAudio.Ricomincia();
        sincronia.Ricomincia();
        StatoCambiato?.Invoke($"Collegato a {remoto} ({trasporto})");
        return nuova.Token;
    }

    private void FineSessione(CancellationToken sessione, string? errore)
    {
        lock (_bloccoSessione)
        {
            // Se nel frattempo è arrivata un'altra connessione, lo stato è già il suo.
            if (_sessione is null || _sessione.Token != sessione)
                return;
            _sessione = null;
            Trasporto = null;
        }
        AnnunciaAttesa(errore);
    }

    private void AnnunciaAttesa(string? errore)
    {
        // Riletti a ogni attesa: il PC potrebbe aver cambiato rete nel frattempo.
        string indirizzi = string.Join("  ·  ", IndirizziLocali().Select(i => $"{i}:{porta}"));
        string trasporti = MotivoSenzaQuic is null ? "TCP e QUIC" : $"solo TCP: {MotivoSenzaQuic}";
        string motivo = errore is null ? "" : $"\nConnessione persa: {errore}";
        StatoCambiato?.Invoke($"In attesa dell'iPhone su {indirizzi} ({trasporti}){motivo}");
    }

    private void Registra(char tipo, long istanteIphone, int dimensione, bool keyframe) =>
        registro.Scrivi(tipo, istanteIphone, Protocollo.Protocollo.AdessoMicrosecondi(),
            sincronia.Migliore?.ScartoMicrosecondi, dimensione, keyframe);

    private double LatenzaMs(long istanteCatturaIphone) => LatenzaMicrosecondi(istanteCatturaIphone) / 1000.0;

    /// <summary>
    /// Manda un ping al secondo finché dura la connessione. Lettura (un altro task) e scrittura
    /// sullo stesso flusso da due task diversi sono consentite, sia per TCP sia per QUIC.
    /// </summary>
    private static async Task MandaPingAsync(Stream flusso, CancellationToken stop)
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or QuicException)
        {
            // Fine della connessione: se ne accorge anche la lettura.
        }
    }

    private async Task RiceviTuttoAsync(Stream flusso, CancellationToken stop)
    {
        while (await Protocollo.Protocollo.RiceviMessaggioAsync(flusso, stop) is Messaggio messaggio)
        {
            switch (messaggio)
            {
                case FrameVideo frame:
                    Registra('v', frame.IstanteCatturaMicrosecondi, frame.DatiH264.Length, frame.Keyframe);
                    double latenzaMs = LatenzaMs(frame.IstanteCatturaMicrosecondi);
                    statistiche.FrameRicevuto(frame.Keyframe, frame.DatiH264.Length, latenzaMs);
                    coda.Aggiungi(frame);
                    break;
                case PacchettoAudio audio:
                    Registra('a', audio.IstanteCatturaMicrosecondi, audio.Dati.Length, false);
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
