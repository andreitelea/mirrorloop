using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MirrorLoop.Protocollo;

/// <summary>
/// I calcoli dell'abbinamento tra iPhone e PC (docs/protocollo.md, "Abbinamento"), uguali byte per
/// byte a quelli dell'app.
///
/// Il PC si riconosce dall'impronta del suo certificato QUIC, l'iPhone da una chiave P-256. Al primo
/// incontro i due mostrano lo stesso codice di 6 cifre e una persona conferma. L'impegno impedisce a
/// un impostore in mezzo di provare chiavi su chiavi finché i due codici coincidono per caso: ognuno
/// fissa il suo numero prima di vedere quello dell'altro, come nell'abbinamento Bluetooth.
/// La firma copre l'impronta vista dall'iPhone: se in mezzo c'è un impostore, il PC vero se ne accorge.
/// </summary>
public static class Abbinamento
{
    public const int LunghezzaChiave = 65;
    public const int LunghezzaNumero = 32;
    public const int LunghezzaHash = 32;
    public const int LunghezzaFirma = 64;

    // Etichette diverse per ogni calcolo: un hash fatto per uno scopo non può valere per un altro.
    private static readonly byte[] EtichettaImpegno = Encoding.ASCII.GetBytes("MirrorLoop impegno v1");
    private static readonly byte[] EtichettaProva = Encoding.ASCII.GetBytes("MirrorLoop prova v1");
    private static readonly byte[] EtichettaCodice = Encoding.ASCII.GetBytes("MirrorLoop codice v1");

    /// <summary>L'impronta del PC: SHA-256 del certificato (DER), come la calcola l'iPhone.</summary>
    public static byte[] Impronta(ReadOnlySpan<byte> certificatoDer) => SHA256.HashData(certificatoDer);

    public static byte[] NuovoNumero() => RandomNumberGenerator.GetBytes(LunghezzaNumero);

    /// <summary>SHA-256(etichetta ‖ chiave ‖ numero dell'iPhone).</summary>
    public static byte[] Impegno(ReadOnlySpan<byte> chiave, ReadOnlySpan<byte> numeroIphone) =>
        SHA256.HashData([.. EtichettaImpegno, .. chiave, .. numeroIphone]);

    /// <summary>Etichetta ‖ impronta del PC ‖ chiave ‖ numero del PC ‖ numero dell'iPhone.</summary>
    public static byte[] DatiDaFirmare(ReadOnlySpan<byte> impronta, ReadOnlySpan<byte> chiave,
        ReadOnlySpan<byte> numeroPc, ReadOnlySpan<byte> numeroIphone) =>
        [.. EtichettaProva, .. impronta, .. chiave, .. numeroPc, .. numeroIphone];

    /// <summary>
    /// Il codice da confrontare sui due schermi: i primi 4 byte di SHA-256(etichetta ‖ impronta ‖
    /// chiave ‖ numero del PC ‖ numero dell'iPhone), come intero big-endian senza segno, modulo un
    /// milione, scritto con 6 cifre (zeri iniziali compresi).
    /// </summary>
    public static string Codice(ReadOnlySpan<byte> impronta, ReadOnlySpan<byte> chiave,
        ReadOnlySpan<byte> numeroPc, ReadOnlySpan<byte> numeroIphone)
    {
        byte[] hash = SHA256.HashData([.. EtichettaCodice, .. impronta, .. chiave, .. numeroPc, .. numeroIphone]);
        return (BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Vera se <paramref name="firma"/> (r ‖ s, 64 byte) è una firma ECDSA P-256 con SHA-256 dei
    /// <see cref="DatiDaFirmare"/> fatta con la chiave privata di <paramref name="chiave"/>.
    /// Una chiave che non è un punto della curva vale come firma sbagliata.
    /// </summary>
    public static bool FirmaValida(byte[] chiave, ReadOnlySpan<byte> impronta, ReadOnlySpan<byte> numeroPc,
        ReadOnlySpan<byte> numeroIphone, ReadOnlySpan<byte> firma)
    {
        if (chiave.Length != LunghezzaChiave || chiave[0] != 0x04 || firma.Length != LunghezzaFirma)
            return false;
        try
        {
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = chiave[1..33], Y = chiave[33..] },
            });
            return ecdsa.VerifyData(DatiDaFirmare(impronta, chiave, numeroPc, numeroIphone), firma,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        // Su Windows un punto fuori dalla curva dà PlatformNotSupportedException (con dentro una
        // CryptographicException), non solo CryptographicException: trovato da un test.
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>La chiave pubblica nel formato del protocollo (0x04 ‖ X ‖ Y). Per i test e il mittente di prova.</summary>
    public static byte[] ChiavePubblica(ECDsa privata)
    {
        ECParameters p = privata.ExportParameters(includePrivateParameters: false);
        return [0x04, .. p.Q.X!, .. p.Q.Y!];
    }

    /// <summary>La firma che manderebbe l'iPhone. Per i test e il mittente di prova.</summary>
    public static byte[] Firma(ECDsa privata, ReadOnlySpan<byte> impronta, ReadOnlySpan<byte> numeroPc, ReadOnlySpan<byte> numeroIphone) =>
        privata.SignData(DatiDaFirmare(impronta, ChiavePubblica(privata), numeroPc, numeroIphone),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}
