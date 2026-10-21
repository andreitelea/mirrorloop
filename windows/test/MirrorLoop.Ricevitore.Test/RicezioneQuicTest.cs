using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using MirrorLoop.Protocollo;
using Ping = MirrorLoop.Protocollo.Ping;

namespace MirrorLoop.Ricevitore.Test;

/// <summary>
/// Prove complete sul PC con QUIC vero: un finto iPhone apre, come l'app, una connessione QUIC per
/// ogni corsia (controllo, video, audio) e le presenta con lo stesso numero di trasmissione.
/// </summary>
public sealed class RicezioneQuicTest : IAsyncDisposable
{
    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());
    private readonly int _porta = PortaLibera();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
    private readonly List<QuicConnection> _connessioni = [];
    private readonly CodaFrame _coda = new();
    private readonly SincroniaOrologi _sincronia = new();
    private readonly StatisticheAudio _statisticheAudio = new();
    private readonly RiproduzioneAudio _riproduzioneAudio = new();
    private readonly RegistroPacchetti _registro;
    private readonly RicezioneRete _ricezione;
    private Task? _inAscolto;

    public RicezioneQuicTest()
    {
        _registro = new RegistroPacchetti(_cartella);
        _ricezione = new RicezioneRete(_porta, _coda, new StatisticheRicezione(), _statisticheAudio,
            _riproduzioneAudio, _sincronia, _registro, CertificatoRicevitore.CaricaOCrea(_cartella));
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
        _stop.Dispose();
        Directory.Delete(_cartella, recursive: true);
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

    /// <summary>Una connessione QUIC nuova con una sola corsia, presentata con l'apertura (tipo 5).</summary>
    private async Task<QuicStream> ApriCorsiaAsync(RuoloCorsia ruolo, long numeroTrasmissione, QuicStreamType tipo = QuicStreamType.Bidirectional)
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
                // Il certificato del ricevitore è autofirmato: qui, come nell'app, si accetta.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        }, _stop.Token);
        _connessioni.Add(connessione);
        var corsia = await connessione.OpenOutboundStreamAsync(tipo, _stop.Token);
        await Protocollo.Protocollo.InviaAperturaCorsiaAsync(corsia, new AperturaCorsia(ruolo, numeroTrasmissione), _stop.Token);
        return corsia;
    }

    /// <summary>Legge finché il ricevitore non chiude la corsia; false se non la chiude entro 5 s.</summary>
    private async Task<bool> ChiusaDalRicevitoreAsync(QuicStream corsia)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limite.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (await Protocollo.Protocollo.RiceviMessaggioAsync(corsia, limite.Token) is not null)
            {
                // Ping arrivati prima della chiusura.
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
        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, numero);
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
    }

    [Fact]
    public async Task UnNumeroNuovoChiudeLaTrasmissionePrecedente()
    {
        if (!AvviaRicevitore())
            return;

        await using var vecchio = await ApriCorsiaAsync(RuoloCorsia.Controllo, 1);
        Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(vecchio, _stop.Token));

        // L'iPhone si ricollega: numero nuovo.
        await using var nuovo = await ApriCorsiaAsync(RuoloCorsia.Controllo, 2);
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

        await using var controllo = await ApriCorsiaAsync(RuoloCorsia.Controllo, numero);
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
    public async Task UnaCorsiaCheNonSiPresentaVieneRifiutata()
    {
        if (!AvviaRicevitore())
            return;

        var connessione = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, _porta),
            DefaultStreamErrorCode = 1,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [RicezioneRete.ProtocolloQuic],
                TargetHost = "mirrorloop",
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        }, _stop.Token);
        _connessioni.Add(connessione);

        // Un frame senza apertura: è il comportamento delle build 0.7.0 e 0.7.1 per la corsia video.
        await using var corsia = await connessione.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _stop.Token);
        await Protocollo.Protocollo.InviaFrameAsync(corsia, new FrameVideo(true, 1, new byte[] { 0, 0, 0, 1, 0x65 }), _stop.Token);

        Assert.True(await ChiusaDalRicevitoreAsync(corsia), "la corsia senza apertura doveva essere chiusa");
        Assert.False(_coda.ProvaPrendi(TimeSpan.FromMilliseconds(200), out _));
        Assert.Null(_ricezione.Trasporto);
    }
}
