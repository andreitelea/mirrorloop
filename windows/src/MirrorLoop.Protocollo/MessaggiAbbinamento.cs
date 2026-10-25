namespace MirrorLoop.Protocollo;

public enum ModoPresentazione : byte
{
    /// <summary>L'iPhone conosce già questo PC: se il PC conosce la chiave, accetta subito.</summary>
    Ricollegamento = 0,
    /// <summary>
    /// L'iPhone non conosce questo PC: il PC mostra sempre il codice e chiede conferma, anche se
    /// conosce già la chiave (per esempio dopo una reinstallazione dell'app, che cancella i PC
    /// conosciuti ma non la chiave nel portachiavi).
    /// </summary>
    NuovoAbbinamento = 1,
}

/// <summary>
/// Primo messaggio dell'iPhone sulla corsia di controllo (tipo 6): chi è e l'impegno sul suo
/// numero segreto, che rivelerà solo dopo aver visto quello del PC (vedi <see cref="Abbinamento"/>).
/// </summary>
/// <param name="Chiave">Chiave pubblica P-256 dell'iPhone, 65 byte non compressi (0x04, X, Y).</param>
/// <param name="Impegno">SHA-256 dell'impegno, 32 byte.</param>
/// <param name="Nome">Nome da mostrare sul PC, al massimo 64 byte in UTF-8.</param>
public sealed record Presentazione(ModoPresentazione Modo, byte[] Chiave, byte[] Impegno, string Nome) : Messaggio;

/// <summary>La risposta del PC alla presentazione (tipo 7): il suo numero casuale di 32 byte.</summary>
public sealed record Sfida(byte[] NumeroPc) : Messaggio;

/// <summary>
/// La prova dell'iPhone (tipo 8): il numero segreto promesso nella presentazione e la firma
/// che dimostra di possedere la chiave.
/// </summary>
/// <param name="NumeroIphone">32 byte, quelli dell'impegno.</param>
/// <param name="Firma">ECDSA P-256 con SHA-256, 64 byte (r e s di seguito).</param>
public sealed record Prova(byte[] NumeroIphone, byte[] Firma) : Messaggio;

public enum ValoreEsito : byte
{
    /// <summary>iPhone riconosciuto (o appena consentito): la trasmissione può partire.</summary>
    Accettato = 0,
    /// <summary>iPhone nuovo: il PC mostra il codice e aspetta che qualcuno consenta o rifiuti.</summary>
    InAttesa = 1,
    /// <summary>Prova sbagliata, richiesta rifiutata o scaduta: il PC chiude.</summary>
    Rifiutato = 2,
}

/// <summary>Il verdetto del PC (tipo 9). Dopo <see cref="ValoreEsito.InAttesa"/> ne arriva un altro.</summary>
public sealed record Esito(ValoreEsito Valore) : Messaggio;
