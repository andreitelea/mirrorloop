using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using MirrorLoop.Protocollo;
using MittenteTest;

// La "telecamera finta": genera l'animazione di prova, la comprime in H.264 e la spedisce
// al ricevitore (in QUIC, sulla sola corsia video) e/o la salva in un file .h264 grezzo da aprire con VLC.
//
// Uso:  MittenteTest [--ricevitore host:porta] [--file percorso.h264] [--secondi N]
//   --ricevitore   trasmette finché non si preme Ctrl+C (o per N secondi)
//   --file         senza --ricevitore registra 10 secondi, se non indicato diversamente

const int Larghezza = 1280;
const int Altezza = 720;
const int Fps = 30;
const int Bitrate = 4_000_000;
const int IntervalloKeyframe = Fps * 2;
const string Uso = "Uso: MittenteTest [--ricevitore host:porta] [--file percorso.h264] [--secondi N]";

// Se il ricevitore non legge per tutto questo tempo, lo consideriamo perso.
TimeSpan limiteInvio = TimeSpan.FromSeconds(2);

string? percorso = null;
string? host = null;
int porta = 0;
int? secondi = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length:
            percorso = args[++i];
            break;
        case "--ricevitore" when i + 1 < args.Length && LeggiIndirizzo(args[i + 1], out host, out porta):
            i++;
            break;
        case "--secondi" when i + 1 < args.Length && int.TryParse(args[i + 1], out int s) && s > 0:
            secondi = s;
            i++;
            break;
        default:
            Console.WriteLine(Uso);
            return 1;
    }
}
if (percorso is null && host is null)
{
    Console.WriteLine(Uso);
    return 1;
}
if (host is null)
    secondi ??= 10;
if (host is not null && !QuicConnection.IsSupported)
{
    Console.WriteLine("QUIC non è disponibile su questo Windows (serve Windows 11).");
    return 1;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C: chiudiamo con ordine invece di lasciare che il programma venga ucciso.
    e.Cancel = true;
    stop.Cancel();
};

var animazione = new Animazione(Larghezza, Altezza);
var statistiche = new Statistiche(Fps);
using FileStream? file = percorso is null ? null : File.Create(percorso);
TimeSpan periodo = TimeSpan.FromSeconds(1.0 / Fps);
var orologio = Stopwatch.StartNew();
TimeSpan? scadenza = secondi is null ? null : TimeSpan.FromSeconds(secondi.Value);
long frameGenerati = 0;
long frameSaltati = 0;

Console.WriteLine($"{Larghezza}×{Altezza}, {Fps} fps, {Bitrate / 1_000_000.0:F1} Mbit/s, keyframe ogni {IntervalloKeyframe} frame");
if (file is not null)
    Console.WriteLine($"Registro in {Path.GetFullPath(percorso!)}");
Console.WriteLine(host is null ? $"Durata: {secondi} secondi" : "Ctrl+C per fermare.");

while (!Finito())
{
    Collegamento? collegamento = null;
    if (host is not null)
    {
        collegamento = Collegati(host, porta);
        if (collegamento is null)
            break;
    }

    // Un compressore nuovo per ogni collegamento: così il primo frame che il ricevitore
    // vede è sempre un keyframe, e può mostrare l'immagine subito.
    using (var compressore = new CompressoreH264(Larghezza, Altezza, Fps, Bitrate, IntervalloKeyframe))
        Trasmetti(compressore, collegamento?.Video);
    collegamento?.Chiudi();

    if (host is null)
        break;
}

orologio.Stop();
statistiche.StampaRiepilogo(orologio.Elapsed, frameGenerati);
Console.WriteLine($"Frame saltati per stare al passo: {frameSaltati}");
return statistiche.KeyframeSenzaIstruzioni == 0 ? 0 : 2;

bool Finito() => stop.IsCancellationRequested || (scadenza is not null && orologio.Elapsed >= scadenza);

// Come l'app iPhone: una connessione QUIC per corsia, presentata dall'apertura (tipo 5) con un
// numero di trasmissione nuovo a ogni collegamento. Qui c'è solo la corsia video.
Collegamento? Collegati(string host, int porta)
{
    bool avvisato = false;
    while (!Finito())
    {
        QuicConnection? connessione = null;
        try
        {
            connessione = QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new DnsEndPoint(host, porta),
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol("mirrorloop/1")],
                    TargetHost = "mirrorloop",
                    // Il certificato del ricevitore è autofirmato: per le prove sul PC si accetta.
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            }, stop.Token).AsTask().GetAwaiter().GetResult();
            QuicStream video = connessione.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token).AsTask().GetAwaiter().GetResult();
            Protocollo.InviaAperturaCorsiaAsync(video, new AperturaCorsia(RuoloCorsia.Video, Random.Shared.NextInt64()), stop.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine($"Collegato al ricevitore {host}:{porta} (QUIC).");
            return new Collegamento(connessione, video);
        }
        catch (Exception ex) when (ex is QuicException or SocketException or AuthenticationException or OperationCanceledException)
        {
            connessione?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (!avvisato && ex is not OperationCanceledException)
            {
                Console.WriteLine($"Ricevitore {host}:{porta} non raggiungibile, riprovo ogni secondo...");
                avvisato = true;
            }
            stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }
    }
    return null;
}

// Trasmette finché non è finito il tempo, non si preme Ctrl+C o non cade la connessione.
void Trasmetti(CompressoreH264 compressore, Stream? rete)
{
    // L'istante di cattura di ogni frame aspetta qui finché il compressore non restituisce
    // il frame compresso. Senza B-frame l'ordine di uscita è uguale a quello di ingresso.
    var istanti = new Queue<long>();
    var secondo = new ContatoreSecondo();
    long appuntamento = (long)(orologio.Elapsed / periodo);

    try
    {
        while (!Finito())
        {
            // Ritmo regolare: ogni frame ha il suo appuntamento sull'orologio. Se siamo in ritardo
            // di più di un frame (per esempio perché il ricevitore legge piano) saltiamo avanti:
            // in diretta è meglio perdere un frame che accumulare ritardo.
            long adesso = (long)(orologio.Elapsed / periodo);
            if (adesso > appuntamento + 1)
            {
                frameSaltati += adesso - appuntamento;
                secondo.Saltati += adesso - appuntamento;
                appuntamento = adesso;
            }
            TimeSpan attesa = appuntamento * periodo - orologio.Elapsed;
            if (attesa > TimeSpan.Zero)
                stop.Token.WaitHandle.WaitOne(attesa);
            if (Finito())
                break;

            long istante = Protocollo.AdessoMicrosecondi();
            DateTime istanteData = DateTime.UnixEpoch.AddTicks(istante * TimeSpan.TicksPerMicrosecond);
            // Il contatore mostra l'appuntamento, non il numero di frame prodotti:
            // così i frame saltati si vedono come "buchi" nella numerazione.
            ReadOnlySpan<byte> frame = animazione.Disegna(appuntamento, Fps, istanteData);
            istanti.Enqueue(istante);
            frameGenerati++;
            appuntamento++;

            foreach (FrameCompresso compresso in compressore.Comprimi(frame))
                Consegna(compresso, istanti.Dequeue(), rete, secondo);

            secondo.StampaSeServe(rete is not null);
        }

        foreach (FrameCompresso compresso in compressore.Concludi())
            Consegna(compresso, istanti.Dequeue(), rete, secondo);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        // Ctrl+C durante un invio: si chiude e basta.
    }
    catch (Exception ex) when (ex is IOException or QuicException or OperationCanceledException)
    {
        // OperationCanceledException qui vuol dire: il ricevitore non ha letto entro il limite.
        Console.WriteLine($"Connessione con il ricevitore persa ({ex.Message}). Riprovo...");
    }
}

void Consegna(FrameCompresso compresso, long istante, Stream? rete, ContatoreSecondo secondo)
{
    file?.Write(compresso.DatiH264);
    statistiche.Aggiungi(compresso);
    secondo.Frame++;
    secondo.Byte += compresso.DatiH264.Length;

    if (rete is not null)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        limite.CancelAfter(limiteInvio);
        var busta = new FrameVideo(compresso.Keyframe, istante, compresso.DatiH264);
        Protocollo.InviaFrameAsync(rete, busta, limite.Token).GetAwaiter().GetResult();
    }
}

static bool LeggiIndirizzo(string testo, out string? host, out int porta)
{
    host = null;
    porta = 0;
    int duePunti = testo.LastIndexOf(':');
    if (duePunti <= 0 || !int.TryParse(testo[(duePunti + 1)..], out porta) || porta is < 1 or > 65535)
        return false;
    host = testo[..duePunti];
    return true;
}

/// <summary>La connessione QUIC con il ricevitore e la sua corsia video.</summary>
sealed record Collegamento(QuicConnection Connessione, QuicStream Video)
{
    public void Chiudi()
    {
        Video.Dispose();
        Connessione.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

/// <summary>Una riga di statistiche al secondo, per seguire la trasmissione dal vivo.</summary>
sealed class ContatoreSecondo
{
    private readonly Stopwatch _orologio = Stopwatch.StartNew();
    public long Frame;
    public long Byte;
    public long Saltati;

    public void StampaSeServe(bool inRete)
    {
        if (_orologio.Elapsed < TimeSpan.FromSeconds(1))
            return;
        double secondi = _orologio.Elapsed.TotalSeconds;
        Console.WriteLine($"{(inRete ? "in onda" : "registro")} · {Frame / secondi:F1} fps · {Byte * 8 / secondi / 1_000_000:F2} Mbit/s · saltati {Saltati}");
        Frame = Byte = Saltati = 0;
        _orologio.Restart();
    }
}
