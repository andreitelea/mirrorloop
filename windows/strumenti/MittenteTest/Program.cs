using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using MirrorLoop.Protocollo;
using MittenteTest;

// La "telecamera finta": genera l'animazione di prova, la comprime in H.264 e la spedisce
// al ricevitore (in QUIC, dopo l'abbinamento: sul ricevitore va consentito il codice mostrato qui)
// e/o la salva in un file .h264 grezzo da aprire con VLC.
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
// La "chiave dell'iPhone" del mittente di prova: nuova a ogni avvio, quindi sempre da consentire sul ricevitore.
using var chiave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
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

// Come l'app iPhone: una connessione QUIC per corsia (controllo e video), presentate dall'apertura
// (tipo 5) con un numero di trasmissione nuovo, e l'abbinamento sulla corsia di controllo. La chiave
// è nuova a ogni avvio: il mittente di prova è sempre un telefono sconosciuto, da consentire sul PC.
Collegamento? Collegati(string host, int porta)
{
    bool avvisato = false;
    while (!Finito())
    {
        var connessioni = new List<QuicConnection>();
        byte[] impronta = [];
        QuicStream Apri(RuoloCorsia ruolo, long numero, QuicStreamType tipo)
        {
            var connessione = QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new DnsEndPoint(host, porta),
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol("mirrorloop/2")],
                    TargetHost = "mirrorloop",
                    // Si accetta il certificato autofirmato, ma se ne prende l'impronta: va nella firma.
                    RemoteCertificateValidationCallback = (_, certificato, _, _) =>
                    {
                        impronta = Abbinamento.Impronta(certificato!.GetRawCertData());
                        return true;
                    },
                },
            }, stop.Token).AsTask().GetAwaiter().GetResult();
            connessioni.Add(connessione);
            QuicStream corsia = connessione.OpenOutboundStreamAsync(tipo, stop.Token).AsTask().GetAwaiter().GetResult();
            Protocollo.InviaAperturaCorsiaAsync(corsia, new AperturaCorsia(ruolo, numero), stop.Token).GetAwaiter().GetResult();
            return corsia;
        }

        try
        {
            long numero = Random.Shared.NextInt64();
            QuicStream controllo = Apri(RuoloCorsia.Controllo, numero, QuicStreamType.Bidirectional);
            if (!AbbinaAsync(controllo, impronta).GetAwaiter().GetResult())
            {
                Console.WriteLine("Il ricevitore ha rifiutato il collegamento.");
                new Collegamento(connessioni, controllo, null, Task.CompletedTask).Chiudi();
                return null;
            }
            QuicStream video = Apri(RuoloCorsia.Video, numero, QuicStreamType.Unidirectional);
            Console.WriteLine($"Collegato al ricevitore {host}:{porta} (QUIC).");
            return new Collegamento(connessioni, controllo, video, RispondiAiPingAsync(controllo));
        }
        catch (Exception ex) when (ex is QuicException or SocketException or AuthenticationException or OperationCanceledException
                                   or IOException or InvalidDataException)
        {
            foreach (var connessione in connessioni)
                connessione.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

// Presentazione, sfida, prova ed esito (docs/protocollo.md, "Abbinamento"), sempre come nuovo abbinamento.
async Task<bool> AbbinaAsync(QuicStream controllo, byte[] impronta)
{
    byte[] pubblica = Abbinamento.ChiavePubblica(chiave);
    byte[] numeroIphone = Abbinamento.NuovoNumero();
    await Protocollo.InviaPresentazioneAsync(controllo,
        new Presentazione(ModoPresentazione.NuovoAbbinamento, pubblica, Abbinamento.Impegno(pubblica, numeroIphone), "MittenteTest"), stop.Token);
    if (await Protocollo.RiceviMessaggioAsync(controllo, stop.Token) is not Sfida sfida)
        return false;
    await Protocollo.InviaProvaAsync(controllo, new Prova(numeroIphone, Abbinamento.Firma(chiave, impronta, sfida.NumeroPc, numeroIphone)), stop.Token);

    var esito = await Protocollo.RiceviMessaggioAsync(controllo, stop.Token) as Esito;
    if (esito?.Valore == ValoreEsito.InAttesa)
    {
        string codice = Abbinamento.Codice(impronta, pubblica, sfida.NumeroPc, numeroIphone);
        Console.WriteLine($"Codice di abbinamento: {codice[..3]} {codice[3..]}. Consenti sul ricevitore se è lo stesso.");
        esito = await Protocollo.RiceviMessaggioAsync(controllo, stop.Token) as Esito;
    }
    return esito?.Valore == ValoreEsito.Accettato;
}

// Risponde ai ping del ricevitore (con l'orologio del PC: lo scarto viene circa zero).
async Task RispondiAiPingAsync(QuicStream controllo)
{
    try
    {
        while (await Protocollo.RiceviMessaggioAsync(controllo, stop.Token) is Messaggio messaggio)
        {
            if (messaggio is Ping ping)
            {
                long adesso = Protocollo.AdessoMicrosecondi();
                await Protocollo.InviaPongAsync(controllo, new Pong(ping.Numero, ping.T1, adesso, adesso), stop.Token);
            }
        }
    }
    catch (Exception ex) when (ex is QuicException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
    {
        // Fine del collegamento: se ne accorge anche l'invio dei frame.
    }
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

/// <summary>Le connessioni QUIC con il ricevitore, le corsie di controllo e video e la risposta ai ping.</summary>
sealed record Collegamento(List<QuicConnection> Connessioni, QuicStream Controllo, QuicStream? Video, Task Ping)
{
    public void Chiudi()
    {
        Video?.Dispose();
        Controllo.Dispose();
        foreach (var connessione in Connessioni)
            connessione.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Ping.GetAwaiter().GetResult();
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
