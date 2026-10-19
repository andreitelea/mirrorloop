using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Uso:  ChatUdp <mia porta> <porta dell'altro>
// Esempio, in due finestre:  ChatUdp 5001 5002   e   ChatUdp 5002 5001
//
// Ogni pacchetto UDP è già un messaggio a sé: niente prefisso di lunghezza come in TCP.
// Il primo byte dice che tipo di pacchetto è.
const byte TipoTesto = (byte)'T';
const byte TipoRaffica = (byte)'R';   // [R][totale: 4 byte][numero: 4 byte][riempitivo]

// Il carico massimo di un pacchetto UDP su IPv4 è 65507 byte.
const int CaricoMassimo = 65507;

// Se per questo tempo non arrivano pacchetti della raffica, la consideriamo finita.
TimeSpan FineRaffica = TimeSpan.FromSeconds(1);

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

if (args.Length != 2 || !int.TryParse(args[0], out int miaPorta) || !int.TryParse(args[1], out int altraPorta))
{
    Console.WriteLine("Uso: ChatUdp <mia porta> <porta dell'altro>");
    Console.WriteLine("Esempio: ChatUdp 5001 5002   (e nell'altra finestra: ChatUdp 5002 5001)");
    return 1;
}

var altro = new IPEndPoint(IPAddress.Loopback, altraPorta);

UdpClient udp;
try
{
    udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, miaPorta));
}
catch (SocketException ex)
{
    Console.WriteLine($"Impossibile usare la porta {miaPorta}: {ex.Message}");
    return 1;
}

using (udp)
{
    if (OperatingSystem.IsWindows())
    {
        // Su Windows, se spediamo a una porta dove non c'è nessuno, la *ricezione successiva*
        // fallisce con "connessione interrotta" (WSAECONNRESET), anche se con UDP non esiste
        // nessuna connessione. SIO_UDP_CONNRESET = FALSE disattiva questa segnalazione.
        // Valore: _WSAIOW(IOC_VENDOR, 12) = 0x9800000C (mstcpip.h).
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
    }

    Console.WriteLine($"In ascolto su 127.0.0.1:{miaPorta}, spedisco a 127.0.0.1:{altraPorta}.");
    Console.WriteLine("Scrivi un messaggio e premi Invio. Comandi: /raffica <quanti> [byte], /esci");
    Console.WriteLine("Nota: con UDP non c'è connessione, quindi non sapremo mai se l'altro c'è.");

    using var stop = new CancellationTokenSource();
    Task ricezione = RiceviTuttoAsync(udp, stop.Token);
    await Task.Run(() => InviaDaTastieraAsync(udp, altro));
    stop.Cancel();
    await ricezione;
}
return 0;

async Task InviaDaTastieraAsync(UdpClient udp, IPEndPoint altro)
{
    while (Console.ReadLine() is string riga)
    {
        if (riga == "/esci")
            return;
        if (riga.Length == 0)
            continue;

        if (riga.StartsWith("/raffica"))
        {
            await InviaRafficaAsync(udp, altro, riga);
            continue;
        }

        byte[] testo = Encoding.UTF8.GetBytes(riga);
        if (1 + testo.Length > CaricoMassimo)
        {
            Console.WriteLine("Messaggio troppo lungo per un singolo pacchetto UDP.");
            continue;
        }

        byte[] pacchetto = [TipoTesto, .. testo];
        // Nessun errore anche se dall'altra parte non c'è nessuno: la cartolina parte e basta.
        await udp.SendAsync(pacchetto, altro);
    }
}

async Task InviaRafficaAsync(UdpClient udp, IPEndPoint altro, string comando)
{
    string[] parti = comando.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    // 1200 byte: circa la dimensione dei pezzi in cui si spezza un frame video per la rete.
    int dimensione = 1200;
    if (parti.Length < 2 || !int.TryParse(parti[1], out int quanti) || quanti <= 0
        || (parti.Length >= 3 && !int.TryParse(parti[2], out dimensione))
        || dimensione < 9 || dimensione > CaricoMassimo)
    {
        Console.WriteLine($"Uso: /raffica <quanti> [byte per pacchetto, da 9 a {CaricoMassimo}, predefinito 1200]");
        return;
    }

    byte[] pacchetto = new byte[dimensione];
    pacchetto[0] = TipoRaffica;
    BinaryPrimitives.WriteInt32BigEndian(pacchetto.AsSpan(1), quanti);

    var cronometro = System.Diagnostics.Stopwatch.StartNew();
    for (int numero = 0; numero < quanti; numero++)
    {
        BinaryPrimitives.WriteInt32BigEndian(pacchetto.AsSpan(5), numero);
        await udp.SendAsync(pacchetto, altro);
    }
    cronometro.Stop();

    double megabyte = (double)quanti * dimensione / (1024 * 1024);
    Console.WriteLine($"Raffica spedita: {quanti} pacchetti da {dimensione} byte ({megabyte:F1} MB) in {cronometro.ElapsedMilliseconds} ms.");
}

async Task RiceviTuttoAsync(UdpClient udp, CancellationToken stop)
{
    // Statistiche della raffica in corso: le stampiamo quando smette di arrivare roba.
    int attesi = 0, ricevuti = 0, fuoriOrdine = 0, ultimoNumero = -1;
    var visti = new HashSet<int>();

    while (!stop.IsCancellationRequested)
    {
        UdpReceiveResult risultato;
        // Durante una raffica aspettiamo al massimo FineRaffica, poi stampiamo il resoconto.
        using var attesa = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (attesi > 0)
            attesa.CancelAfter(FineRaffica);

        try
        {
            risultato = await udp.ReceiveAsync(attesa.Token);
        }
        catch (OperationCanceledException)
        {
            if (attesi > 0)
            {
                StampaResoconto(attesi, ricevuti, fuoriOrdine);
                attesi = ricevuti = fuoriOrdine = 0;
                ultimoNumero = -1;
                visti.Clear();
            }
            continue;
        }

        byte[] dati = risultato.Buffer;
        if (dati.Length == 0)
            continue;

        if (dati[0] == TipoTesto)
        {
            Console.WriteLine($"[altro] {Encoding.UTF8.GetString(dati, 1, dati.Length - 1)}");
        }
        else if (dati[0] == TipoRaffica && dati.Length >= 9)
        {
            attesi = BinaryPrimitives.ReadInt32BigEndian(dati.AsSpan(1));
            int numero = BinaryPrimitives.ReadInt32BigEndian(dati.AsSpan(5));

            // UDP non promette nemmeno che una cartolina arrivi una volta sola.
            if (!visti.Add(numero))
                continue;

            ricevuti++;
            if (numero < ultimoNumero)
                fuoriOrdine++;
            else
                ultimoNumero = numero;
        }
    }
}

static void StampaResoconto(int attesi, int ricevuti, int fuoriOrdine)
{
    int persi = attesi - ricevuti;
    double percentuale = 100.0 * persi / attesi;
    Console.WriteLine($"Raffica ricevuta: {ricevuti} su {attesi}, persi {persi} ({percentuale:F2}%), fuori ordine {fuoriOrdine}.");
}
