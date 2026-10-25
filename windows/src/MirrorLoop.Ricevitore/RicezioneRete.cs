using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirrorLoop.Protocollo;
using Ping = MirrorLoop.Protocollo.Ping;
// Nel ricevitore "Presentazione" è il thread che manda in onda le immagini.
using PresentazioneTelefono = MirrorLoop.Protocollo.Presentazione;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Ascolta il mittente e legge i messaggi dalla rete in continuazione. Leggere subito è ciò che
/// impedisce al ritardo di accumularsi nei buffer di Windows.
///
/// Solo QUIC su UDP (TCP tolto il 25/10/2026: non cifrato, e senza vantaggi misurati): l'iPhone
/// apre corsie indipendenti (controllo, video, audio) così una perdita nel video non blocca l'audio.
/// Le corsie possono arrivare su connessioni diverse: le riunisce il numero di trasmissione
/// scritto nella loro apertura (tipo 5).
/// Prima di tutto l'abbinamento (tipi 6–9 sulla corsia di controllo): solo un iPhone abbinato, o
/// consentito da chi sta al PC, apre una trasmissione. Le altre corsie aspettano il suo esito.
/// Un solo mittente alla volta: una nuova trasmissione accettata chiude la precedente.
/// </summary>
/// <param name="chiediConsenso">
/// Chiede a chi sta al PC se consentire un iPhone nuovo (vero = consenti). Il token si annulla se la
/// richiesta scade o il telefono sparisce. Null: i telefoni nuovi sono rifiutati.
/// </param>
/// <param name="attesaConsenso">Quanto aspettare la risposta; 60 s se non indicato (i test lo accorciano).</param>
public sealed class RicezioneRete(
    int porta,
    CodaFrame coda,
    StatisticheRicezione statistiche,
    StatisticheAudio statisticheAudio,
    RiproduzioneAudio riproduzioneAudio,
    SincroniaOrologi sincronia,
    RegistroPacchetti registro,
    X509Certificate2? certificato,
    TelefoniAbbinati telefoni,
    Func<RichiestaAbbinamento, CancellationToken, Task<bool>>? chiediConsenso,
    TimeSpan? attesaConsenso = null)
{
    /// <summary>Nome del protocollo applicativo negoziato in QUIC (ALPN): l'iPhone deve usare lo stesso.</summary>
    public static readonly SslApplicationProtocol ProtocolloQuic = new("mirrorloop/2");

    // Un ping al secondo basta: lo scarto tra gli orologi cambia molto lentamente.
    private static readonly TimeSpan IntervalloPing = TimeSpan.FromSeconds(1);

    // Una corsia QUIC che non si presenta entro questo tempo non è dell'app: si chiude. Lo stesso
    // limite vale per ogni messaggio dell'abbinamento.
    private static readonly TimeSpan LimiteApertura = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _attesaConsenso = attesaConsenso ?? TimeSpan.FromSeconds(60);

    // L'impronta del certificato, come la vede l'iPhone: è ciò che firma nella prova.
    private readonly byte[] _impronta = certificato is null ? [] : Abbinamento.Impronta(certificato.RawData);

    // Una sola richiesta di conferma alla volta: le altre, nel frattempo, si rifiutano subito.
    private readonly SemaphoreSlim _unaRichiesta = new(1, 1);

    // Per ogni numero di trasmissione, l'esito dell'abbinamento della sua corsia di controllo, che
    // le corsie video e audio aspettano. Le voci vecchie si tolgono a ogni abbinamento nuovo.
    private readonly object _bloccoEsiti = new();
    private readonly Dictionary<long, (TaskCompletionSource<bool> Esito, DateTime Creato)> _esiti = [];

    private readonly object _bloccoSessione = new();
    private CancellationTokenSource? _sessione;
    // Solo per le sessioni QUIC: le corsie con questo numero appartengono alla sessione in corso.
    private long? _numeroTrasmissione;

    /// <summary>Descrizione dello stato, per la finestra. Viene chiamato da un thread in sottofondo.</summary>
    public event Action<string>? StatoCambiato;

    /// <summary>"QUIC" o null se nessuno è collegato.</summary>
    public string? Trasporto { get; private set; }

    /// <summary>Se QUIC non è disponibile (e quindi il ricevitore non può ricevere), il motivo.</summary>
    public string? MotivoSenzaQuic { get; private set; }

    public async Task EseguiAsync(CancellationToken stop)
    {
        if (!QuicListener.IsSupported)
            MotivoSenzaQuic = "QUIC non supportato da questo Windows (serve Windows 11)";
        else if (certificato is null)
            MotivoSenzaQuic = "certificato non disponibile";

        AnnunciaAttesa(null);
        if (MotivoSenzaQuic is null)
            await AscoltaQuicAsync(certificato!, stop);
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

    // ---- QUIC ----

    /// <summary>
    /// In ascolto su tutte le reti del PC, così l'iPhone lo raggiunge dalla rete di casa.
    /// Al primo avvio Windows chiede il permesso del firewall: va consentito solo sulle reti private.
    /// </summary>
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
            // Senza notizie per qualche secondo il mittente si considera sparito.
            IdleTimeout = TimeSpan.FromSeconds(5),
            // Ogni corsia ha la sua connessione: quella del video resta muta quando lo schermo è
            // fermo. I PING di QUIC (a cui l'iPhone risponde da solo) la tengono viva; un iPhone
            // sparito davvero non risponde più e la connessione scade comunque.
            KeepAliveInterval = TimeSpan.FromSeconds(1),
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

    /// <summary>
    /// Una connessione QUIC vive finché vive la trasmissione delle sue corsie: quando la trasmissione
    /// finisce (o ne arriva una nuova) le corsie la chiudono annullando <c>fine</c>.
    /// </summary>
    private async Task GestisciQuicAsync(QuicConnection connessione, CancellationToken stop)
    {
        await using (connessione)
        {
            using var fine = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var corsie = new List<Task>();
            try
            {
                while (true)
                {
                    QuicStream corsia = await connessione.AcceptInboundStreamAsync(fine.Token);
                    corsie.Add(GestisciCorsiaAsync(corsia, connessione.RemoteEndPoint, fine, stop));
                }
            }
            catch (Exception ex) when (ex is QuicException or OperationCanceledException)
            {
                // Connessione chiusa dal mittente, scaduta per inattività o persa; oppure trasmissione
                // finita. Gli errori da mostrare li raccolgono le corsie.
            }
            finally
            {
                await fine.CancelAsync();
                await Task.WhenAll(corsie);
            }
        }
    }

    private async Task GestisciCorsiaAsync(QuicStream corsia, EndPoint remoto, CancellationTokenSource fineConnessione, CancellationToken stop)
    {
        await using (corsia)
        {
            AperturaCorsia apertura;
            try
            {
                using var limite = CancellationTokenSource.CreateLinkedTokenSource(fineConnessione.Token);
                limite.CancelAfter(LimiteApertura);
                if (await Protocollo.Protocollo.RiceviMessaggioAsync(corsia, limite.Token) is not AperturaCorsia letta)
                    return;
                apertura = letta;
            }
            catch (Exception ex) when (ex is QuicException or IOException or EndOfStreamException or InvalidDataException or OperationCanceledException)
            {
                return;
            }

            bool accettata = apertura.Ruolo == RuoloCorsia.Controllo
                ? await AbbinaAsync(corsia, apertura.NumeroTrasmissione, fineConnessione.Token)
                : await AspettaAbbinamentoAsync(apertura.NumeroTrasmissione, fineConnessione.Token);
            if (!accettata)
                return;

            CancellationToken sessione = NuovaSessione(stop, remoto, apertura.NumeroTrasmissione);
            // Finita la trasmissione, si chiude anche la connessione che porta questa corsia.
            using var chiudiConnessione = sessione.Register(fineConnessione.Cancel);
            using var fine = CancellationTokenSource.CreateLinkedTokenSource(sessione);
            Task ping = apertura.Ruolo == RuoloCorsia.Controllo ? MandaPingAsync(corsia, fine.Token) : Task.CompletedTask;
            string? errore = null;
            try
            {
                await RiceviTuttoAsync(corsia, sessione);
            }
            catch (QuicException ex)
            {
                // Chiusura normale del mittente (codice 0) o vera perdita della connessione.
                errore = ex.QuicError == QuicError.ConnectionAborted && ex.ApplicationErrorCode == 0 ? null : ex.Message;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
            {
                errore = ex.Message;
            }
            catch (OperationCanceledException)
            {
                // Trasmissione sostituita da una nuova, o chiusura della finestra.
            }
            finally
            {
                await fine.CancelAsync();
                await ping;
                // Senza una delle sue corsie la trasmissione non sta in piedi: si chiude tutta e
                // l'iPhone si ricollega con un numero nuovo.
                FineSessione(sessione, errore);
            }
        }
    }

    // ---- Abbinamento ----

    /// <summary>L'esito dell'abbinamento della trasmissione con questo numero (creato se manca).</summary>
    private TaskCompletionSource<bool> EsitoDi(long numeroTrasmissione)
    {
        lock (_bloccoEsiti)
        {
            if (_esiti.TryGetValue(numeroTrasmissione, out var voce))
                return voce.Esito;
            DateTime adesso = DateTime.UtcNow;
            // Oltre il tempo massimo di un abbinamento nessuna corsia aspetta più queste voci.
            foreach (long vecchio in _esiti.Where(e => adesso - e.Value.Creato > _attesaConsenso * 2 + LimiteApertura * 4).Select(e => e.Key).ToList())
                _esiti.Remove(vecchio);
            var nuovo = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _esiti[numeroTrasmissione] = (nuovo, adesso);
            return nuovo;
        }
    }

    /// <summary>Una corsia video o audio: aspetta che la corsia di controllo sia accettata.</summary>
    private async Task<bool> AspettaAbbinamentoAsync(long numeroTrasmissione, CancellationToken ct)
    {
        try
        {
            return await EsitoDi(numeroTrasmissione).Task.WaitAsync(_attesaConsenso + LimiteApertura * 3, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// La corsia di controllo: presentazione, sfida, prova ed esito (docs/protocollo.md, "Abbinamento").
    /// Vero se l'iPhone è accettato; in ogni caso l'esito sblocca le corsie che lo aspettano.
    /// </summary>
    private async Task<bool> AbbinaAsync(QuicStream corsia, long numeroTrasmissione, CancellationToken ct)
    {
        bool accettato = false;
        try
        {
            accettato = await PresentazioneAsync(corsia, ct);
        }
        catch (Exception ex) when (ex is QuicException or IOException or EndOfStreamException or InvalidDataException or OperationCanceledException)
        {
            // Telefono sparito, messaggio malformato o arrivato tardi: rifiutato.
        }
        finally
        {
            EsitoDi(numeroTrasmissione).TrySetResult(accettato);
        }
        if (!accettato)
            await RifiutaAsync(corsia);
        return accettato;
    }

    private async Task<bool> PresentazioneAsync(QuicStream corsia, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(LimiteApertura);
        if (await Protocollo.Protocollo.RiceviMessaggioAsync(corsia, limite.Token) is not PresentazioneTelefono presentazione)
            return false;

        byte[] numeroPc = Abbinamento.NuovoNumero();
        await Protocollo.Protocollo.InviaSfidaAsync(corsia, new Sfida(numeroPc), ct);
        limite.CancelAfter(LimiteApertura);
        if (await Protocollo.Protocollo.RiceviMessaggioAsync(corsia, limite.Token) is not Prova prova)
            return false;

        // L'impegno dice che il numero dell'iPhone era deciso prima di vedere quello del PC; la firma
        // che l'iPhone ha la chiave e ha visto proprio questo PC (non un impostore in mezzo).
        if (!CryptographicOperations.FixedTimeEquals(Abbinamento.Impegno(presentazione.Chiave, prova.NumeroIphone), presentazione.Impegno)
            || !Abbinamento.FirmaValida(presentazione.Chiave, _impronta, numeroPc, prova.NumeroIphone, prova.Firma))
            return false;

        if (presentazione.Modo == ModoPresentazione.Ricollegamento && telefoni.Contiene(presentazione.Chiave))
        {
            await Protocollo.Protocollo.InviaEsitoAsync(corsia, new Esito(ValoreEsito.Accettato), ct);
            return true;
        }

        if (chiediConsenso is null || !_unaRichiesta.Wait(0))
            return false;
        try
        {
            await Protocollo.Protocollo.InviaEsitoAsync(corsia, new Esito(ValoreEsito.InAttesa), ct);
            string nome = NomeDaMostrare(presentazione.Nome);
            var richiesta = new RichiestaAbbinamento(
                Abbinamento.Codice(_impronta, presentazione.Chiave, numeroPc, prova.NumeroIphone), nome);

            bool consentito;
            using (var attesa = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attesa.CancelAfter(_attesaConsenso);
                try
                {
                    consentito = await chiediConsenso(richiesta, attesa.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    consentito = false; // Nessuna risposta in tempo.
                }
            }
            if (!consentito)
                return false;

            telefoni.Aggiungi(presentazione.Chiave, nome);
            await Protocollo.Protocollo.InviaEsitoAsync(corsia, new Esito(ValoreEsito.Accettato), ct);
            return true;
        }
        finally
        {
            _unaRichiesta.Release();
        }
    }

    private static async Task RifiutaAsync(QuicStream corsia)
    {
        try
        {
            using var limite = new CancellationTokenSource(LimiteApertura);
            await Protocollo.Protocollo.InviaEsitoAsync(corsia, new Esito(ValoreEsito.Rifiutato), limite.Token);
        }
        catch (Exception ex) when (ex is QuicException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Il telefono non c'è più: niente da dirgli.
        }
    }

    /// <summary>Il nome mandato dal telefono, senza caratteri di controllo; "iPhone" se vuoto.</summary>
    private static string NomeDaMostrare(string nome)
    {
        string pulito = new string(nome.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return pulito.Length > 0 ? pulito : "iPhone";
    }

    // ---- In comune ----

    /// <summary>
    /// Una trasmissione nuova chiude la precedente e riparte con lo stato pulito. Una corsia con il
    /// numero della trasmissione in corso si aggiunge invece a quella.
    /// </summary>
    private CancellationToken NuovaSessione(CancellationToken stop, EndPoint? remoto, long numeroTrasmissione)
    {
        CancellationTokenSource nuova;
        CancellationTokenSource? vecchia;
        lock (_bloccoSessione)
        {
            if (_sessione is not null && _numeroTrasmissione == numeroTrasmissione)
                return _sessione.Token;

            nuova = CancellationTokenSource.CreateLinkedTokenSource(stop);
            vecchia = _sessione;
            _sessione = nuova;
            _numeroTrasmissione = numeroTrasmissione;
            Trasporto = "QUIC";
            // Dentro il blocco: le altre corsie della stessa trasmissione aspettano qui, così i loro
            // primi dati (il keyframe!) non vengono cancellati da questo azzeramento.
            coda.Ricomincia();
            riproduzioneAudio.Ricomincia();
            statisticheAudio.Ricomincia();
            sincronia.Ricomincia();
        }
        vecchia?.Cancel();
        StatoCambiato?.Invoke($"Collegato a {remoto} (QUIC)");
        return nuova.Token;
    }

    private void FineSessione(CancellationToken sessione, string? errore)
    {
        CancellationTokenSource finita;
        lock (_bloccoSessione)
        {
            // Se nel frattempo è arrivata un'altra trasmissione, lo stato è già il suo.
            if (_sessione is null || _sessione.Token != sessione)
                return;
            finita = _sessione;
            _sessione = null;
            _numeroTrasmissione = null;
            Trasporto = null;
        }
        // Chiude le altre corsie e connessioni della stessa trasmissione.
        finita.Cancel();
        AnnunciaAttesa(errore);
    }

    private void AnnunciaAttesa(string? errore)
    {
        // Riletti a ogni attesa: il PC potrebbe aver cambiato rete nel frattempo.
        string indirizzi = string.Join("  ·  ", IndirizziLocali().Select(i => $"{i}:{porta}"));
        if (MotivoSenzaQuic is not null)
        {
            StatoCambiato?.Invoke($"Impossibile ricevere: {MotivoSenzaQuic}");
            return;
        }
        string motivo = errore is null ? "" : $"\nConnessione persa: {errore}";
        StatoCambiato?.Invoke($"In attesa dell'iPhone su {indirizzi} (QUIC){motivo}");
    }

    private void Registra(char tipo, long istanteIphone, int dimensione, bool keyframe) =>
        registro.Scrivi(tipo, istanteIphone, Protocollo.Protocollo.AdessoMicrosecondi(),
            sincronia.Migliore?.ScartoMicrosecondi, dimensione, keyframe);

    private double LatenzaMs(long istanteCatturaIphone) => LatenzaMicrosecondi(istanteCatturaIphone) / 1000.0;

    /// <summary>
    /// Manda un ping al secondo finché dura la connessione. Lettura (un altro task) e scrittura
    /// sulla stessa corsia QUIC da due task diversi sono consentite.
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
