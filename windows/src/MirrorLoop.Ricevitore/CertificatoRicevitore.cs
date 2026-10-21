using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Il certificato con cui il ricevitore si presenta nelle connessioni QUIC (QUIC è sempre cifrato,
/// con TLS 1.3, e il lato server deve avere un certificato).
///
/// Si crea da solo al primo avvio (autofirmato, chiave ECDSA P-256) e si conserva in
/// %LOCALAPPDATA%\MirrorLoop\certificato.pfx, così resta lo stesso tra un avvio e l'altro: servirà
/// quando l'app imparerà a riconoscere il "suo" ricevitore.
/// </summary>
public static class CertificatoRicevitore
{
    public const string NomeFile = "certificato.pfx";

    public static X509Certificate2 CaricaOCrea(string? cartella = null)
    {
        cartella ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorLoop");
        string percorso = Path.Combine(cartella, NomeFile);
        if (!File.Exists(percorso))
        {
            Directory.CreateDirectory(cartella);
            File.WriteAllBytes(percorso, Crea().Export(X509ContentType.Pfx));
        }
        // Il certificato si ricarica sempre dal file PFX: il sistema crittografico di Windows (SChannel),
        // usato da QUIC, non accetta le chiavi "di passaggio" create solo in memoria.
        return X509CertificateLoader.LoadPkcs12FromFile(percorso, password: null);
    }

    private static X509Certificate2 Crea()
    {
        using var chiave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var richiesta = new CertificateRequest("CN=MirrorLoop ricevitore", chiave, HashAlgorithmName.SHA256);
        richiesta.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        richiesta.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        // Server TLS (OID 1.3.6.1.5.5.7.3.1).
        richiesta.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var adesso = DateTimeOffset.UtcNow;
        return richiesta.CreateSelfSigned(adesso.AddDays(-1), adesso.AddYears(5));
    }
}
