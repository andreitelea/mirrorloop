using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using MirrorLoop.Protocollo;
using Ping = MirrorLoop.Protocollo.Ping;

namespace MirrorLoop.Ricevitore.Test;

/// <summary>
/// Prova completa sul PC: un finto iPhone si collega in QUIC, apre la corsia di controllo e quella
/// video come farà l'app, risponde ai ping e manda un keyframe.
/// </summary>
public sealed class RicezioneQuicTest : IDisposable
{
    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());

    public void Dispose() => Directory.Delete(_cartella, recursive: true);

    private static int PortaLibera()
    {
        var prova = new TcpListener(IPAddress.Loopback, 0);
        prova.Start();
        int porta = ((IPEndPoint)prova.LocalEndpoint).Port;
        prova.Stop();
        return porta;
    }

    [Fact]
    public async Task UnMittenteQuicArrivaFinoAllaCodaEAgliOrologi()
    {
        // QUIC richiede Windows 11 o Server 2022 (build 20348+): su sistemi più vecchi il test non ha
        // senso, ma su quelli nuovi la sua assenza è un errore, non un motivo per saltare.
        if (!QuicConnection.IsSupported)
        {
            Assert.True(Environment.OSVersion.Version.Build < 20348, "QUIC dovrebbe essere disponibile su questo Windows");
            return;
        }

        int porta = PortaLibera();
        var coda = new CodaFrame();
        var sincronia = new SincroniaOrologi();
        using var riproduzioneAudio = new RiproduzioneAudio();
        using var registro = new RegistroPacchetti(_cartella);
        var ricezione = new RicezioneRete(porta, coda, new StatisticheRicezione(), new StatisticheAudio(),
            riproduzioneAudio, sincronia, registro, CertificatoRicevitore.CaricaOCrea(_cartella));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task inAscolto = ricezione.EseguiAsync(stop.Token);

        await using var connessione = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, porta),
            DefaultStreamErrorCode = 1,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [RicezioneRete.ProtocolloQuic],
                TargetHost = "mirrorloop",
                // Il certificato del ricevitore è autofirmato: qui, come nell'app, si accetta.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        }, stop.Token);

        // Corsia di controllo: il ricevitore manda ping, il finto iPhone risponde con l'orologio spostato di 1 s.
        await using var controllo = await connessione.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
        await Protocollo.Protocollo.InviaPongAsync(controllo, new Pong(0, 0, 0, 0), stop.Token); // apre la corsia
        var ping = Assert.IsType<Ping>(await Protocollo.Protocollo.RiceviMessaggioAsync(controllo, stop.Token));
        long oraIphone = Protocollo.Protocollo.AdessoMicrosecondi() + 1_000_000;
        await Protocollo.Protocollo.InviaPongAsync(controllo, new Pong(ping.Numero, ping.T1, oraIphone, oraIphone), stop.Token);

        // Corsia video, separata.
        await using var video = await connessione.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token);
        await Protocollo.Protocollo.InviaFrameAsync(video, new FrameVideo(true, 42, new byte[] { 0, 0, 0, 1, 0x65 }), stop.Token);

        Assert.True(coda.ProvaPrendi(TimeSpan.FromSeconds(5), out FrameVideo? arrivato));
        Assert.Equal(42, arrivato!.IstanteCatturaMicrosecondi);
        Assert.Equal("QUIC", ricezione.Trasporto);

        // Lo scarto misurato deve essere circa +1 s (sul PC stesso il viaggio dura pochissimo).
        for (int i = 0; i < 50 && sincronia.Migliore is null; i++)
            await Task.Delay(100, stop.Token);
        Assert.InRange(sincronia.Migliore!.Value.ScartoMicrosecondi, 900_000, 1_100_000);

        await stop.CancelAsync();
        await inAscolto;
    }
}
