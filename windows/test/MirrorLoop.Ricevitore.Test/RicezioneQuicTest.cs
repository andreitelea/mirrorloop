using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using MirrorLoop.Protocollo;
using Ping = MirrorLoop.Protocollo.Ping;
using PresentazioneTelefono = MirrorLoop.Protocollo.Presentazione;

namespace MirrorLoop.Ricevitore.Test;

/// <summary>
/// Prove complete sul PC con QUIC vero: un finto iPhone apre, come l'app, una connessione QUIC per
/// ogni corsia (controllo, video, audio), le presenta con lo stesso numero di trasmissione e si
/// abbina sulla corsia di controllo con la sua chiave.
/// </summary>
public sealed class RicezioneQuicTest : IAsyncDisposable
{
    private static readonly TimeSpan AttesaConsenso = TimeSpan.FromSeconds(2);

    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());
    private readonly int _porta = PortaLibera();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
    private readonly List<QuicConnection> _connessioni = [];
    private readonly CodaFrame _coda = new();
    private readonly SincroniaOrologi _sincronia = new();
    private readonly StatisticheAudio _statisticheAudio = new();
    private readonly RiproduzioneAudio _riproduzioneAudio = new();
    private readonly RegistroPacchetti _registro;
    private readonly TelefoniAbbinati _telefoni;
    private readonly RicezioneRete _ricezione;
    // Il finto iPhone già abbinato.
    private readonly ECDsa _chiaveIphone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<RichiestaAbbinamento> _richieste = [];
    // Che cosa risponde "chi sta al PC" alle richieste di abbinamento; null = rifiuta.
    private Func<RichiestaAbbinamento, CancellationToken, Task<bool>>? _risposta;
    // L'impronta del certificato che il finto iPhone ha visto nella connessione, come nell'app.
    private byte[] _improntaVista = [];
    private Task? _inAscolto;

    public RicezioneQuicTest()
    {
        _registro = new RegistroPacchetti(_cartella);
        _telefoni = new TelefoniAbbinati(_cartella);
        _telefoni.Aggiungi(Abbinamento.ChiavePubblica(_chiaveIphone), "iPhone di prova");
        _ricezione = new RicezioneRete(_porta, _coda, new StatisticheRicezione(), _statisticheAudio,
            _riproduzioneAudio, _sincronia, _registro, CertificatoRicevitore.CaricaOCrea(_cartella),
            _telefoni, ChiediConsensoAsync, AttesaConsenso);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connessione in _connessioni)
            await connessione.DisposeAsync();
        await _stop.CancelAsync();
        if (_inAscolto is not null)
            await _inAscolto;
        _riproduzioneAudio.Dispose();
        _registro.Dispose();
        _chiaveIphone.Dispose();
        _stop.Dispose();
        Directory.Delete(_cartella, recursive: true);
    }

    private Task<bool> ChiediConsensoAsync(RichiestaAbbinamento richiesta, CancellationToken ct)
    {
        lock (_richieste)
            _richieste.Add(richiesta);
        return _risposta?.Invoke(richiesta, ct) ?? Task.FromResult(false);
    }

    private int NumeroRichieste
    {
        get { lock (_richieste) return _richieste.Count; }
    }

    private static int PortaLibera()
    {
        var prova = new TcpListener(IPAddress.Loopback, 0);
        prova.Start();
        int porta = ((IPEndPoint)prova.LocalEndpoint).Port;
        prova.Stop();
        return porta;
    }

    /// <summary>
    /// QUIC richiede Windows 11 o Server 2022 (build 20348+): su sistemi più vecchi i test non hanno
    /// senso, ma su quelli nuovi la sua assenza è un errore, non un motivo per saltare.
    /// </summary>
    private bool AvviaRicevitore()
    {
        if (!QuicConnection.IsSupported)
        {
            Assert.True(Environment.OSVersion.Version.Build < 20348, "QUIC dovrebbe essere disponibile su questo Windows");
            return false;
        }
        _inAscolto = _ricezione.EseguiAsync(_stop.Token);
        return true;
    }

    private async Task<QuicConnection> ConnettiAsync()
    {
        var connessione = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, _porta),
            DefaultStreamErrorCode = 1,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [RicezioneRete.ProtocolloQuic],
                TargetHost = "mirrorloop",
                // Si accetta qualsiasi certificato, ma se ne annota l'impronta: la firma la conterrà.
                RemoteCertificateValidationCallback = (_, certificato, _, _) =>
                {
                    _improntaVista = Abbinamento.Impronta(certificato!.GetRawCertData());
                    return true;
                },
            },
        }, _stop.Token);
        _connessioni.Add(connessione);
        return connessione;
    }

    /// <summary>Una connessione QUIC nuova con una sola corsia, presentata con l'apertura (tipo 5).</summary>
    private async Task<QuicStream> ApriCorsiaAsync(RuoloCorsia ruolo, long numeroTrasmissione, QuicStreamType tipo = QuicStreamType.Bidirectional)
    {
        var connessione = await ConnettiAsync();
        var corsia = await connessione.OpenOutboundStreamAsync(tipo, _stop.Token);
        await Protocollo.Protocollo.InviaAperturaCorsiaAsync(corsia, new AperturaCorsia(ruolo, numeroTrasmissione), _stop.Token);
        return corsia;
    }

    /// <summary>
    /// Presentazione, sfida, prova e primo esito, come l'app. Restituisce l'esito e il codice che
    /// l'iPhone mostrerebbe.
    /// </summary>
    private async Task<(ValoreEsito Esito, string Codice)> PresentatiAsync(QuicStream controllo,
        ModoPresentazione modo = ModoPresentazione.Ricollegamento, ECDsa? chiave = null,
        bool impegnoSbagliato = false, bool firmaSbagliata = false)
    {
        chiave ??= _chiaveIphone;
        byte[] pubblica = Abbinamento.ChiavePubblica(chiave);
        byte[] numeroIphone = Abbinamento.NuovoNumero();
        byte[] impegno = impegnoSbagliato ? Abbinamento.NuovoNumero() : Abbinamento.Impegno(pubblica, numeroIphone);
        await Protocollo.Protocollo.InviaPresentazioneAsync(controllo, new PresentazioneTelefono(modo, pubblica, impegno, "iPhone"), _stop.Token);

        var sfida = Assert.IsType<Sfida>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, _stop.Token));
        byte[] impronta = firmaSbagliata ? Abbinamento.NuovoNumero() : _improntaVista;
        byte[] firma = Abbinamento.Firma(chiave, impronta, sfida.NumeroPc, numeroIphone);
        await Protocollo.Protocollo.InviaProvaAsync(controllo, new Prova(numeroIphone, firma), _stop.Token);

        var esito = Assert.IsType<Esito>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, _stop.Token));
        return (esito.Valore, Abbinamento.Codice(_improntaVista, pubblica, sfida.NumeroPc, numeroIphone));
    }

    /// <summary>La corsia di controllo del finto iPhone già abbinato, accettata.</summary>
    private async Task<QuicStream> ApriControlloAbbinatoAsync(long numeroTrasmissione)
    {
        var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, numeroTrasmissione);
        Assert.Equal(ValoreEsito.Accettato, (await PresentatiAsync(controllo)).Esito);
        return controllo;
    }

    private static async Task<ValoreEsito> EsitoSuccessivoAsync(QuicStream controllo, CancellationToken ct) =>
        Assert.IsType<Esito>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, ct)).Valore;

    /// <summary>Legge finché il ricevitore non chiude la corsia; false se non la chiude entro 5 s.</summary>
    private async Task<bool> ChiusaDalRicevitoreAsync(QuicStream corsia)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limite.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (await Protocollo.Protocollo.RiceviMessaggioAsync(corsia, limite.Token) is not null)
            {
                // Ping o esiti arrivati prima della chiusura.
            }
            return true;
        }
        catch (QuicException)
        {
            return true;
        }
        catch (OperationCanceledException) when (limite.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            return false;
        }
    }

    [Fact]
    public async Task TreConnessioniConLoStessoNumeroFormanoUnaTrasmissione()
    {
        if (!AvviaRicevitore())
            return;
        const long numero = 0x1234_5678_9ABC_DEF0;

        // Controllo: il ricevitore manda ping, il finto iPhone risponde con l'orologio spostato di 1 s.
        await using var controllo = await ApriControlloAbbinatoAsync(numero);
        var ping = Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, _stop.Token));
        long oraIphone = Protocollo.Protocollo.AdessoMicrosecondi() + 1_000_000;
        await Protocollo.Protocollo.InviaPongAsync(controllo, new Pong(ping.Numero, ping.T1, oraIphone, oraIphone), _stop.Token);

        // Video bidirezionale come nell'app, audio unidirezionale: il ricevitore accetta entrambi.
        await using var video = await ApriCorsiaAsync(RuoloCorsia.Video, numero);
        await Protocollo.Protocollo.InviaFrameAsync(video, new FrameVideo(true, 42, new byte[] { 0, 0, 0, 1, 0x65 }), _stop.Token);
        await using var audio = await ApriCorsiaAsync(RuoloCorsia.Audio, numero, QuicStreamType.Unidirectional);
        await Protocollo.Protocollo.InviaAudioAsync(audio, new PacchettoAudio(CodificaAudio.Pcm16, 2, 48_000, 43, new byte[4]), _stop.Token);

        Assert.True(_coda.ProvaPrendi(TimeSpan.FromSeconds(5), out FrameVideo? arrivato));
        Assert.Equal(42, arrivato!.IstanteCatturaMicrosecondi);
        Assert.Equal("QUIC", _ricezione.Trasporto);

        long pacchettiAudio = 0;
        for (int i = 0; i < 50 && pacchettiAudio == 0; i++)
        {
            pacchettiAudio += _statisticheAudio.Leggi().Pacchetti;
            if (pacchettiAudio == 0)
                await Task.Delay(100, _stop.Token);
        }
        Assert.Equal(1, pacchettiAudio);

        // Una sola trasmissione: la coda è ripartita una volta sola, non a ogni connessione.
        Assert.Equal(1, _coda.Generazione);

        // Lo scarto misurato deve essere circa +1 s (sul PC stesso il viaggio dura pochissimo).
        for (int i = 0; i < 50 && _sincronia.Migliore is null; i++)
            await Task.Delay(100, _stop.Token);
        Assert.InRange(_sincronia.Migliore!.Value.ScartoMicrosecondi, 900_000, 1_100_000);
        Assert.Equal(0, NumeroRichieste);
    }

    [Fact]
    public async Task UnNumeroNuovoChiudeLaTrasmissionePrecedente()
    {
        if (!AvviaRicevitore())
            return;

        await using var vecchio = await ApriControlloAbbinatoAsync(1);
        Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(vecchio, _stop.Token));

        // L'iPhone si ricollega: numero nuovo.
        await using var nuovo = await ApriControlloAbbinatoAsync(2);
        Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(nuovo, _stop.Token));

        Assert.True(await ChiusaDalRicevitoreAsync(vecchio), "la trasmissione vecchia doveva essere chiusa");
        Assert.Equal(2, _coda.Generazione);
        Assert.Equal("QUIC", _ricezione.Trasporto);
    }

    [Fact]
    public async Task LaFineDiUnaCorsiaChiudeTuttaLaTrasmissione()
    {
        if (!AvviaRicevitore())
            return;
        const long numero = 7;

        await using var controllo = await ApriControlloAbbinatoAsync(numero);
        Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, _stop.Token));
        var video = await ApriCorsiaAsync(RuoloCorsia.Video, numero);
        await Protocollo.Protocollo.InviaFrameAsync(video, new FrameVideo(true, 1, new byte[] { 0, 0, 0, 1, 0x65 }), _stop.Token);
        Assert.True(_coda.ProvaPrendi(TimeSpan.FromSeconds(5), out _));

        // La connessione video si chiude (per esempio persa): anche il controllo deve chiudersi.
        await _connessioni[^1].CloseAsync(0, _stop.Token);

        Assert.True(await ChiusaDalRicevitoreAsync(controllo), "il controllo doveva chiudersi insieme al video");
        Assert.Null(_ricezione.Trasporto);
    }

    [Fact]
    public async Task UnaCorsiaMutaOltreLInattivitaRestaViva()
    {
        if (!AvviaRicevitore())
            return;
        const long numero = 9;

        await using var controllo = await ApriControlloAbbinatoAsync(numero);
        Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, _stop.Token));
        await using var video = await ApriCorsiaAsync(RuoloCorsia.Video, numero);
        await Protocollo.Protocollo.InviaFrameAsync(video, new FrameVideo(true, 1, new byte[] { 0, 0, 0, 1, 0x65 }), _stop.Token);
        Assert.True(_coda.ProvaPrendi(TimeSpan.FromSeconds(5), out _));

        // Schermo fermo: l'iPhone non manda frame e la connessione del video resta muta più a lungo
        // dei 5 s di inattività. Deve restare viva lo stesso, senza far ripartire la trasmissione.
        await Task.Delay(TimeSpan.FromSeconds(6.5), _stop.Token);
        await Protocollo.Protocollo.InviaFrameAsync(video, new FrameVideo(false, 2, new byte[] { 0, 0, 0, 1, 0x41 }), _stop.Token);

        Assert.True(_coda.ProvaPrendi(TimeSpan.FromSeconds(5), out FrameVideo? dopo), "il frame dopo la pausa doveva arrivare");
        Assert.Equal(2, dopo!.IstanteCatturaMicrosecondi);
        Assert.Equal(1, _coda.Generazione);
        Assert.Equal("QUIC", _ricezione.Trasporto);
    }

    [Fact]
    public async Task UnaCorsiaCheNonSiPresentaVieneRifiutata()
    {
        if (!AvviaRicevitore())
            return;

        var connessione = await ConnettiAsync();

        // Un frame senza apertura: è il comportamento delle build 0.7.0 e 0.7.1 per la corsia video.
        await using var corsia = await connessione.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _stop.Token);
        await Protocollo.Protocollo.InviaFrameAsync(corsia, new FrameVideo(true, 1, new byte[] { 0, 0, 0, 1, 0x65 }), _stop.Token);

        Assert.True(await ChiusaDalRicevitoreAsync(corsia), "la corsia senza apertura doveva essere chiusa");
        Assert.False(_coda.ProvaPrendi(TimeSpan.FromMilliseconds(200), out _));
        Assert.Null(_ricezione.Trasporto);
    }

    [Fact]
    public async Task UnTelefonoNuovoConsentitoVieneAccettatoESalvatoESoloDopoSiVedono()
    {
        if (!AvviaRicevitore())
            return;
        var consenso = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _risposta = (_, _) => consenso.Task;
        using var nuovo = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const long numero = 11;

        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, numero);
        // La corsia video arriva e manda un frame mentre il PC aspetta la conferma.
        await using var video = await ApriCorsiaAsync(RuoloCorsia.Video, numero);
        await Protocollo.Protocollo.InviaFrameAsync(video, new FrameVideo(true, 5, new byte[] { 0, 0, 0, 1, 0x65 }), _stop.Token);

        var (esito, codiceIphone) = await PresentatiAsync(controllo, ModoPresentazione.NuovoAbbinamento, nuovo);

        Assert.Equal(ValoreEsito.InAttesa, esito);
        Assert.Equal(1, NumeroRichieste);
        Assert.Equal(codiceIphone, _richieste[0].Codice);
        Assert.Equal("iPhone", _richieste[0].Nome);
        Assert.False(_coda.ProvaPrendi(TimeSpan.FromMilliseconds(300), out _), "niente immagini prima della conferma");
        Assert.Null(_ricezione.Trasporto);

        consenso.SetResult(true);

        Assert.Equal(ValoreEsito.Accettato, await EsitoSuccessivoAsync(controllo, _stop.Token));
        Assert.True(_coda.ProvaPrendi(TimeSpan.FromSeconds(5), out FrameVideo? arrivato));
        Assert.Equal(5, arrivato!.IstanteCatturaMicrosecondi);
        Assert.True(_telefoni.Contiene(Abbinamento.ChiavePubblica(nuovo)));
        Assert.True(new TelefoniAbbinati(_cartella).Contiene(Abbinamento.ChiavePubblica(nuovo)));
    }

    [Fact]
    public async Task UnTelefonoNuovoRifiutatoNonVedeNulla()
    {
        if (!AvviaRicevitore())
            return;
        _risposta = (_, _) => Task.FromResult(false);
        using var nuovo = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const long numero = 12;

        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, numero);
        await using var video = await ApriCorsiaAsync(RuoloCorsia.Video, numero);
        await Protocollo.Protocollo.InviaFrameAsync(video, new FrameVideo(true, 5, new byte[] { 0, 0, 0, 1, 0x65 }), _stop.Token);

        Assert.Equal(ValoreEsito.InAttesa, (await PresentatiAsync(controllo, ModoPresentazione.NuovoAbbinamento, nuovo)).Esito);
        Assert.Equal(ValoreEsito.Rifiutato, await EsitoSuccessivoAsync(controllo, _stop.Token));

        Assert.True(await ChiusaDalRicevitoreAsync(video), "la corsia video doveva essere chiusa");
        Assert.False(_coda.ProvaPrendi(TimeSpan.FromMilliseconds(200), out _));
        Assert.Null(_ricezione.Trasporto);
        Assert.False(_telefoni.Contiene(Abbinamento.ChiavePubblica(nuovo)));
        Assert.Equal(MotivoRifiuto.RifiutatoDalPC, _ricezione.UltimoRifiuto?.Motivo);
        Assert.Equal("iPhone", _ricezione.UltimoRifiuto?.NomeTelefono);
    }

    [Fact]
    public async Task SenzaRispostaLaRichiestaScadeEIlTelefonoERifiutato()
    {
        if (!AvviaRicevitore())
            return;
        var annullata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _risposta = async (_, ct) =>
        {
            // Come la finestra: aspetta un clic che non arriva, finché la richiesta non scade.
            using var avviso = ct.Register(() => annullata.TrySetResult());
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        };
        using var nuovo = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, 13);
        Assert.Equal(ValoreEsito.InAttesa, (await PresentatiAsync(controllo, ModoPresentazione.NuovoAbbinamento, nuovo)).Esito);
        var inizio = DateTime.UtcNow;

        Assert.Equal(ValoreEsito.Rifiutato, await EsitoSuccessivoAsync(controllo, _stop.Token));
        Assert.InRange(DateTime.UtcNow - inizio, AttesaConsenso - TimeSpan.FromMilliseconds(500), AttesaConsenso * 3);
        Assert.True(annullata.Task.IsCompleted, "la finestra doveva sapere che la richiesta è scaduta");
        Assert.Equal(MotivoRifiuto.Scaduto, _ricezione.UltimoRifiuto?.Motivo);
        Assert.Equal("2 s", _ricezione.UltimoRifiuto?.Dettaglio);
    }

    [Fact]
    public async Task UnTelefonoGiaNotoCheChiedeUnNuovoAbbinamentoMostraIlCodice()
    {
        // Per esempio dopo una reinstallazione dell'app: l'iPhone non conosce più il PC.
        if (!AvviaRicevitore())
            return;
        _risposta = (_, _) => Task.FromResult(true);

        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, 14);
        var (esito, codice) = await PresentatiAsync(controllo, ModoPresentazione.NuovoAbbinamento);

        Assert.Equal(ValoreEsito.InAttesa, esito);
        Assert.Equal(ValoreEsito.Accettato, await EsitoSuccessivoAsync(controllo, _stop.Token));
        Assert.Equal(codice, _richieste.Single().Codice);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnaProvaSbagliataVieneRifiutataSenzaChiedereNulla(bool impegnoSbagliato, bool firmaSbagliata)
    {
        if (!AvviaRicevitore())
            return;
        _risposta = (_, _) => Task.FromResult(true);

        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, 15);
        var (esito, _) = await PresentatiAsync(controllo, impegnoSbagliato: impegnoSbagliato, firmaSbagliata: firmaSbagliata);

        Assert.Equal(ValoreEsito.Rifiutato, esito);
        Assert.Equal(0, NumeroRichieste);
        Assert.Equal(MotivoRifiuto.VerificaFallita, _ricezione.UltimoRifiuto?.Motivo);
        Assert.True(await ChiusaDalRicevitoreAsync(controllo));
        Assert.Null(_ricezione.Trasporto);
    }

    [Fact]
    public async Task UnoSconosciutoNonInterrompeLaTrasmissioneInCorso()
    {
        if (!AvviaRicevitore())
            return;
        _risposta = (_, _) => Task.FromResult(false);

        await using var controllo = await ApriControlloAbbinatoAsync(21);
        Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, _stop.Token));
        Assert.Equal(1, _coda.Generazione);

        using var sconosciuto = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var intruso = await ApriCorsiaAsync(RuoloCorsia.Controllo, 22);
        var (esito, _) = await PresentatiAsync(intruso, ModoPresentazione.Ricollegamento, sconosciuto);
        Assert.Equal(ValoreEsito.InAttesa, esito);
        Assert.Equal(ValoreEsito.Rifiutato, await EsitoSuccessivoAsync(intruso, _stop.Token));

        // La trasmissione di prima continua: stessa generazione, ping ancora in arrivo.
        Assert.Equal(1, _coda.Generazione);
        Assert.Equal("QUIC", _ricezione.Trasporto);
        Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, _stop.Token));
    }

    [Fact]
    public async Task UnaSecondaRichiestaMentreLaPrimaAspettaVieneRifiutataSubito()
    {
        if (!AvviaRicevitore())
            return;
        var consenso = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _risposta = (_, _) => consenso.Task;
        using var primo = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondo = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using var controlloPrimo = await ApriCorsiaAsync(RuoloCorsia.Controllo, 31);
        Assert.Equal(ValoreEsito.InAttesa, (await PresentatiAsync(controlloPrimo, ModoPresentazione.NuovoAbbinamento, primo)).Esito);

        await using var controlloSecondo = await ApriCorsiaAsync(RuoloCorsia.Controllo, 32);
        Assert.Equal(ValoreEsito.Rifiutato, (await PresentatiAsync(controlloSecondo, ModoPresentazione.NuovoAbbinamento, secondo)).Esito);
        Assert.Equal(1, NumeroRichieste);
        Assert.Equal(MotivoRifiuto.AltraRichiestaInCorso, _ricezione.UltimoRifiuto?.Motivo);

        consenso.SetResult(true);
        Assert.Equal(ValoreEsito.Accettato, await EsitoSuccessivoAsync(controlloPrimo, _stop.Token));
    }

    [Fact]
    public async Task UnTelefonoCheNonSiPresentaInTempoVieneRifiutatoConIlMotivo()
    {
        // Il caso dell'anomalia del 25/10: un rifiuto senza codice, di cui la finestra non diceva il perché.
        if (!AvviaRicevitore())
            return;
        var segnalati = new List<RifiutoAbbinamento>();
        _ricezione.AbbinamentoRifiutato += rifiuto => { lock (segnalati) segnalati.Add(rifiuto); };

        // Apertura della corsia di controllo e poi silenzio, invece della presentazione.
        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, 41);

        Assert.Equal(ValoreEsito.Rifiutato, await EsitoSuccessivoAsync(controllo, _stop.Token));
        var rifiuto = Assert.Single(segnalati);
        Assert.Equal(MotivoRifiuto.NessunaPresentazione, rifiuto.Motivo);
        Assert.Null(rifiuto.NomeTelefono);
        Assert.Equal("l'iPhone non si è presentato in tempo (5 s)", rifiuto.Descrizione);
        Assert.Same(rifiuto, _ricezione.UltimoRifiuto);
    }
}
