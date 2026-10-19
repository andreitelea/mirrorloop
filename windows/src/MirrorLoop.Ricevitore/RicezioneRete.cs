using System.IO;
using System.Net;
using System.Net.Sockets;
using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Ascolta il mittente e legge i frame dalla rete in continuazione, mettendoli nella coda.
/// Non fa altro: leggere subito è ciò che impedisce al ritardo di accumularsi nei buffer di Windows.
/// </summary>
public sealed class RicezioneRete(int porta, CodaFrame coda, StatisticheRicezione statistiche)
{
    /// <summary>Descrizione dello stato, per la finestra. Viene chiamato da un thread in sottofondo.</summary>
    public event Action<string>? StatoCambiato;

    public async Task EseguiAsync(CancellationToken stop)
    {
        // Per ora solo connessioni da questo PC: niente domande dal firewall.
        // Aprire alla rete di casa (per l'iPhone) è un passo della Fase 3.
        var ascolto = new TcpListener(IPAddress.Loopback, porta);
        ascolto.Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                StatoCambiato?.Invoke($"In attesa del mittente su 127.0.0.1:{porta}...");
                using TcpClient client = await ascolto.AcceptTcpClientAsync(stop);
                client.NoDelay = true;
                StatoCambiato?.Invoke($"Collegato a {client.Client.RemoteEndPoint}");

                coda.Ricomincia();
                try
                {
                    await RiceviTuttoAsync(client.GetStream(), stop);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
                {
                    // Il mittente è sparito o ha mandato dati non validi: si torna ad aspettare.
                    StatoCambiato?.Invoke($"Connessione persa: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), stop);
                }
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

    private async Task RiceviTuttoAsync(NetworkStream flusso, CancellationToken stop)
    {
        while (await Protocollo.Protocollo.RiceviFrameAsync(flusso, stop) is FrameVideo frame)
        {
            double latenzaMs = (Protocollo.Protocollo.AdessoMicrosecondi() - frame.IstanteCatturaMicrosecondi) / 1000.0;
            statistiche.FrameRicevuto(frame.Keyframe, frame.DatiH264.Length, latenzaMs);
            coda.Aggiungi(frame);
        }
    }
}
