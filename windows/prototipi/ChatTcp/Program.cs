using System.Net;
using System.Net.Sockets;
using System.Text;
using ChatTcp;

// Uso:  ChatTcp server [porta]   oppure   ChatTcp client [porta]
const int PortaPredefinita = 5000;

// Senza UTF-8 la console di Windows rovina le lettere accentate.
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 1 || (args[0] != "server" && args[0] != "client"))
{
    Console.WriteLine("Uso: ChatTcp server|client [porta]");
    return 1;
}

int porta = PortaPredefinita;
if (args.Length >= 2 && !int.TryParse(args[1], out porta))
{
    Console.WriteLine($"Porta non valida: {args[1]}");
    return 1;
}

TcpClient connessione;
try
{
    connessione = args[0] == "server"
        ? await AspettaClientAsync(porta)
        : await CollegatiAlServerAsync(porta);
}
catch (SocketException ex)
{
    Console.WriteLine($"Errore di rete: {ex.Message}");
    if (args[0] == "client")
        Console.WriteLine("Il server è avviato? Lancia prima \"ChatTcp server\" in un'altra finestra.");
    return 1;
}

using (connessione)
{
    // Disattiva l'algoritmo di Nagle, che raggruppa i messaggi piccoli aspettando
    // qualche millisecondo: per una diretta preferiamo spedire subito.
    connessione.NoDelay = true;
    await ChattaAsync(connessione);
}
return 0;

static async Task<TcpClient> AspettaClientAsync(int porta)
{
    // IPAddress.Loopback = 127.0.0.1: accettiamo solo connessioni da questo PC.
    var ascoltatore = new TcpListener(IPAddress.Loopback, porta);
    ascoltatore.Start();
    Console.WriteLine($"Server in ascolto su 127.0.0.1:{porta}. In attesa del client...");

    TcpClient client = await ascoltatore.AcceptTcpClientAsync();
    // Una chat a due: dopo il primo client smettiamo di accettarne altri.
    ascoltatore.Stop();

    Console.WriteLine($"Client collegato da {client.Client.RemoteEndPoint}.");
    return client;
}

static async Task<TcpClient> CollegatiAlServerAsync(int porta)
{
    var client = new TcpClient();
    Console.WriteLine($"Collegamento a 127.0.0.1:{porta}...");
    await client.ConnectAsync(IPAddress.Loopback, porta);
    Console.WriteLine("Collegato al server.");
    return client;
}

static async Task ChattaAsync(TcpClient connessione)
{
    NetworkStream flusso = connessione.GetStream();
    Console.WriteLine("Scrivi un messaggio e premi Invio. /esci per uscire.");

    // Il "barista": un compito ascolta la rete, l'altro legge la tastiera.
    // Lettura e scrittura sullo stesso NetworkStream possono avvenire insieme
    // purché ci sia un solo lettore e un solo scrittore.
    Task ricezione = RiceviTuttoAsync(flusso);
    Task invio = Task.Run(() => InviaDaTastieraAsync(flusso));

    // Si esce appena uno dei due finisce: o abbiamo scritto /esci, o l'altro se n'è andato.
    // Se finisce la ricezione, la lettura da tastiera resta bloccata su ReadLine, ma il
    // processo termina lo stesso perché gira su un thread in background.
    await Task.WhenAny(ricezione, invio);
}

static async Task RiceviTuttoAsync(NetworkStream flusso)
{
    try
    {
        while (await Messaggi.RiceviAsync(flusso) is string messaggio)
            Console.WriteLine($"[altro] {messaggio}");

        Console.WriteLine("L'altro ha chiuso la chat.");
    }
    catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
    {
        // Succede, per esempio, se l'altra finestra viene chiusa di colpo.
        Console.WriteLine($"Connessione persa: {ex.Message}");
    }
    catch (ObjectDisposedException)
    {
        // Abbiamo chiuso noi la connessione con /esci: niente da segnalare.
    }
}

static async Task InviaDaTastieraAsync(NetworkStream flusso)
{
    try
    {
        while (Console.ReadLine() is string riga)
        {
            if (riga == "/esci")
                break;
            if (riga.Length == 0)
                continue;
            await Messaggi.InviaAsync(flusso, riga);
        }
    }
    catch (IOException ex)
    {
        Console.WriteLine($"Impossibile inviare: {ex.Message}");
    }
}
