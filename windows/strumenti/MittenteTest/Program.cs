using System.Diagnostics;
using MittenteTest;

// Uso:  MittenteTest --file <percorso.h264> [--secondi N]
//
// Genera l'animazione di prova, la comprime in H.264 e la salva in un file "grezzo" (Annex B)
// che si può aprire con VLC. L'invio al ricevitore arriverà nel passo 2c.

const int Larghezza = 1280;
const int Altezza = 720;
const int Fps = 30;
const int Bitrate = 4_000_000;
const int IntervalloKeyframe = Fps * 2;

string? percorso = null;
int secondi = 10;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length:
            percorso = args[++i];
            break;
        case "--secondi" when i + 1 < args.Length && int.TryParse(args[i + 1], out int s) && s > 0:
            secondi = s;
            i++;
            break;
        default:
            Console.WriteLine("Uso: MittenteTest --file <percorso.h264> [--secondi N]");
            return 1;
    }
}
if (percorso is null)
{
    Console.WriteLine("Uso: MittenteTest --file <percorso.h264> [--secondi N]");
    return 1;
}

var animazione = new Animazione(Larghezza, Altezza);
using var compressore = new CompressoreH264(Larghezza, Altezza, Fps, Bitrate, IntervalloKeyframe);
using var file = File.Create(percorso);
var statistiche = new Statistiche(Fps);

Console.WriteLine($"{Larghezza}×{Altezza}, {Fps} fps, {Bitrate / 1_000_000.0:F1} Mbit/s, keyframe ogni {IntervalloKeyframe} frame");
Console.WriteLine(compressore.Descrizione);
Console.WriteLine($"Registro {secondi} secondi in {Path.GetFullPath(percorso)}...");

int totaleFrame = secondi * Fps;
var orologio = Stopwatch.StartNew();
for (long n = 0; n < totaleFrame; n++)
{
    // Ritmo regolare: ogni frame ha il suo appuntamento, calcolato dall'inizio e non dal frame
    // precedente, così i piccoli ritardi non si accumulano.
    TimeSpan appuntamento = TimeSpan.FromSeconds((double)n / Fps);
    TimeSpan attesa = appuntamento - orologio.Elapsed;
    if (attesa > TimeSpan.Zero)
        Thread.Sleep(attesa);

    ReadOnlySpan<byte> frame = animazione.Disegna(n, Fps, DateTime.UtcNow);
    foreach (FrameCompresso compresso in compressore.Comprimi(frame))
        Registra(compresso);
}
foreach (FrameCompresso compresso in compressore.Concludi())
    Registra(compresso);
orologio.Stop();

statistiche.StampaRiepilogo(orologio.Elapsed, totaleFrame);
return statistiche.KeyframeSenzaIstruzioni == 0 ? 0 : 2;

void Registra(FrameCompresso compresso)
{
    file.Write(compresso.DatiH264);
    statistiche.Aggiungi(compresso);
}
