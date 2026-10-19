using System.Buffers.Binary;
using System.Text;

namespace ChatTcp;

/// <summary>
/// Invio e lettura di messaggi su un flusso TCP con il "prefisso di lunghezza":
/// [4 byte di lunghezza, big-endian][contenuto]. Vedi docs/concetti.md.
/// </summary>
public static class Messaggi
{
    // Un limite evita che 4 byte sbagliati (o malevoli) ci facciano allocare gigabyte.
    // Per i frame video nella Fase 1 andrà alzato.
    private const int LunghezzaMassima = 1024 * 1024;

    public static async Task InviaAsync(Stream flusso, string testo, CancellationToken ct = default)
    {
        byte[] contenuto = Encoding.UTF8.GetBytes(testo);
        if (contenuto.Length > LunghezzaMassima)
            throw new ArgumentException($"Messaggio troppo lungo ({contenuto.Length} byte).", nameof(testo));

        // Prefisso e contenuto in un'unica scrittura: TCP non garantisce comunque i confini,
        // ma così evitiamo di spedire 4 byte da soli in un pacchetto a parte.
        byte[] pacchetto = new byte[4 + contenuto.Length];
        BinaryPrimitives.WriteInt32BigEndian(pacchetto, contenuto.Length);
        contenuto.CopyTo(pacchetto, 4);

        await flusso.WriteAsync(pacchetto, ct);
    }

    /// <summary>
    /// Legge un messaggio intero. Restituisce null se l'altro ha chiuso la connessione
    /// in modo pulito, cioè tra un messaggio e l'altro.
    /// </summary>
    public static async Task<string?> RiceviAsync(Stream flusso, CancellationToken ct = default)
    {
        byte[] prefisso = new byte[4];

        // Zero byte letti = l'altro ha chiuso. Da 1 a 3 byte = si è interrotto a metà prefisso.
        int letti = await flusso.ReadAtLeastAsync(prefisso, 4, throwOnEndOfStream: false, ct);
        if (letti == 0)
            return null;
        if (letti < 4)
            throw new EndOfStreamException("Connessione interrotta a metà di un messaggio.");

        int lunghezza = BinaryPrimitives.ReadInt32BigEndian(prefisso);
        if (lunghezza < 0 || lunghezza > LunghezzaMassima)
            throw new InvalidDataException($"Lunghezza non valida: {lunghezza} byte.");

        // ReadExactlyAsync aspetta finché arrivano tutti i byte: è qui che risolviamo
        // il problema del "nastro senza divisori".
        byte[] contenuto = new byte[lunghezza];
        await flusso.ReadExactlyAsync(contenuto, ct);

        return Encoding.UTF8.GetString(contenuto);
    }
}
