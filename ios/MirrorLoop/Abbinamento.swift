import CryptoKit
import Foundation
import Security

/// I calcoli dell'abbinamento tra iPhone e PC (docs/protocollo.md, "Abbinamento"), uguali byte per
/// byte a quelli del ricevitore (windows/src/MirrorLoop.Protocollo/Abbinamento.cs). I test
/// (MirrorLoopTests) controllano l'esempio con valori fissi del documento.
enum Abbinamento {
    static let lunghezzaNumero = 32

    /// L'impronta del PC: SHA-256 del suo certificato (DER).
    static func impronta(certificatoDer: Data) -> Data {
        Data(SHA256.hash(data: certificatoDer))
    }

    static func nuovoNumero() -> Data {
        Data(SymmetricKey(size: .bits256).withUnsafeBytes { Array($0) })
    }

    /// SHA-256("MirrorLoop impegno v1" ‖ chiave ‖ numero dell'iPhone).
    static func impegno(chiave: Data, numeroIphone: Data) -> Data {
        Data(SHA256.hash(data: Data("MirrorLoop impegno v1".utf8) + chiave + numeroIphone))
    }

    /// "MirrorLoop prova v1" ‖ impronta del PC ‖ chiave ‖ numero del PC ‖ numero dell'iPhone.
    static func datiDaFirmare(impronta: Data, chiave: Data, numeroPc: Data, numeroIphone: Data) -> Data {
        Data("MirrorLoop prova v1".utf8) + impronta + chiave + numeroPc + numeroIphone
    }

    /// I primi 4 byte di SHA-256("MirrorLoop codice v1" ‖ impronta ‖ chiave ‖ numero del PC ‖
    /// numero dell'iPhone), big-endian senza segno, modulo un milione, con 6 cifre.
    static func codice(impronta: Data, chiave: Data, numeroPc: Data, numeroIphone: Data) -> String {
        let hash = SHA256.hash(data: Data("MirrorLoop codice v1".utf8) + impronta + chiave + numeroPc + numeroIphone)
        let numero = hash.prefix(4).reduce(UInt32(0)) { $0 << 8 | UInt32($1) } % 1_000_000
        let cifre = String(numero)
        return String(repeating: "0", count: 6 - cifre.count) + cifre
    }

    /// Il codice in due gruppi da tre, come sul PC.
    static func leggibile(_ codice: String) -> String {
        "\(codice.prefix(3)) \(codice.suffix(3))"
    }

    /// La verifica che fa il PC. Nell'app serve solo ai test.
    static func firmaValida(chiave: Data, impronta: Data, numeroPc: Data, numeroIphone: Data, firma: Data) -> Bool {
        guard let pubblica = try? P256.Signing.PublicKey(x963Representation: chiave),
              let ecdsa = try? P256.Signing.ECDSASignature(rawRepresentation: firma) else { return false }
        return pubblica.isValidSignature(ecdsa, for: datiDaFirmare(impronta: impronta, chiave: chiave, numeroPc: numeroPc, numeroIphone: numeroIphone))
    }

    // MARK: - Messaggi (formato in docs/protocollo.md)

    enum Modo: UInt8 {
        /// L'iPhone conosce già il PC: se il PC conosce l'iPhone, accetta subito.
        case ricollegamento = 0
        /// PC nuovo per l'iPhone: il PC mostra sempre il codice.
        case nuovoAbbinamento = 1
    }

    enum Esito: UInt8 {
        case accettato = 0
        case inAttesa = 1
        case rifiutato = 2
    }

    /// Tipo 6: lunghezza + tipo + modo + chiave (65) + impegno (32) + nome (UTF-8, al massimo 64 byte).
    static func messaggioPresentazione(modo: Modo, chiave: Data, impegno: Data, nome: String) -> Data {
        let nomeUtf8 = Data(nome.utf8.prefix(64))
        return messaggio(tipo: 6, contenuto: Data([modo.rawValue]) + chiave + impegno + nomeUtf8)
    }

    /// Tipo 8: lunghezza + tipo + numero dell'iPhone (32) + firma (64).
    static func messaggioProva(numeroIphone: Data, firma: Data) -> Data {
        messaggio(tipo: 8, contenuto: numeroIphone + firma)
    }

    private static func messaggio(tipo: UInt8, contenuto: Data) -> Data {
        var dati = Data()
        withUnsafeBytes(of: UInt32(1 + contenuto.count).bigEndian) { dati.append(contentsOf: $0) }
        dati.append(tipo)
        dati.append(contentsOf: contenuto)
        return dati
    }
}

// MARK: - La chiave dell'iPhone

/// Chi firma la prova. In produzione la chiave del Secure Enclave; nei test una chiave normale.
protocol ChiaveFirma: Sendable {
    /// La chiave pubblica, 65 byte non compressi (0x04 ‖ X ‖ Y).
    var pubblica: Data { get }
    /// Firma ECDSA P-256 con SHA-256, 64 byte (r ‖ s).
    func firma(_ dati: Data) throws -> Data
}

struct ChiaveSoftware: ChiaveFirma, @unchecked Sendable {
    let privata: P256.Signing.PrivateKey
    var pubblica: Data { privata.publicKey.x963Representation }
    func firma(_ dati: Data) throws -> Data { try privata.signature(for: dati).rawRepresentation }
}

/// La chiave nasce e resta nel Secure Enclave: si può usare per firmare ma non copiare. Nel
/// portachiavi ne sta solo un riferimento cifrato, utilizzabile solo da questo iPhone.
struct ChiaveSecureEnclave: ChiaveFirma, @unchecked Sendable {
    let privata: SecureEnclave.P256.Signing.PrivateKey
    var pubblica: Data { privata.publicKey.x963Representation }
    func firma(_ dati: Data) throws -> Data { try privata.signature(for: dati).rawRepresentation }
}

enum ChiaveIphone {
    private static let servizio = "io.github.andreitelea.mirrorloop"
    private static let account = "chiave-iphone"

    /// La chiave dell'iPhone, creata al primo uso. "Dopo il primo sblocco" perché i ricollegamenti
    /// possono avvenire in background; "solo questo dispositivo": niente backup né iCloud.
    static func carica() throws -> any ChiaveFirma {
        if let salvata = leggi() {
            return ChiaveSecureEnclave(privata: try SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: salvata))
        }
        var errore: Unmanaged<CFError>?
        guard let accesso = SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
                                                            .privateKeyUsage, &errore) else {
            throw errore!.takeRetainedValue() as Error
        }
        let nuova = try SecureEnclave.P256.Signing.PrivateKey(accessControl: accesso)
        try salva(nuova.dataRepresentation)
        return ChiaveSecureEnclave(privata: nuova)
    }

    /// Vero se la chiave c'è già (anche dopo una reinstallazione: il portachiavi sopravvive).
    static var esiste: Bool { leggi() != nil }

    /// Cancella il riferimento: senza, la chiave nel Secure Enclave non è più utilizzabile da
    /// nessuno. Al prossimo collegamento ne nasce una nuova.
    static func cancella() {
        let domanda: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: servizio,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(domanda as CFDictionary)
    }

    private static func leggi() -> Data? {
        let domanda: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: servizio,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
        ]
        var risultato: CFTypeRef?
        return SecItemCopyMatching(domanda as CFDictionary, &risultato) == errSecSuccess ? risultato as? Data : nil
    }

    private static func salva(_ dati: Data) throws {
        let voce: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: servizio,
            kSecAttrAccount as String: account,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecValueData as String: dati,
        ]
        let stato = SecItemAdd(voce as CFDictionary, nil)
        guard stato == errSecSuccess else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(stato))
        }
    }
}

// MARK: - I PC abbinati

/// I PC abbinati, riconosciuti dall'impronta del certificato, con il nome con cui sono stati
/// abbinati (per accorgersi se un PC con lo stesso nome ha cambiato certificato).
/// Nelle impostazioni dell'app: si cancellano se l'app viene disinstallata (la chiave no, ed è
/// per questo che esiste il modo "nuovo abbinamento").
enum PCAbbinati {
    private static let chiave = "pcAbbinati"

    private static var elenco: [String: String] {
        UserDefaults.standard.dictionary(forKey: chiave) as? [String: String] ?? [:]
    }

    static var numero: Int { elenco.count }

    static func conosciuto(_ impronta: Data) -> Bool {
        elenco[esadecimale(impronta)] != nil
    }

    /// Vero se un PC abbinato con questo nome aveva un'altra impronta.
    static func improntaCambiata(nome: String, impronta: Data) -> Bool {
        elenco.contains { $0.value == nome && $0.key != esadecimale(impronta) }
    }

    /// Memorizza il PC; un PC abbinato prima con lo stesso nome viene sostituito.
    static func aggiungi(_ impronta: Data, nome: String) {
        var nuovo = elenco.filter { $0.value != nome }
        nuovo[esadecimale(impronta)] = nome
        UserDefaults.standard.set(nuovo, forKey: chiave)
    }

    static func dimenticaTutti() {
        UserDefaults.standard.removeObject(forKey: chiave)
    }

    private static func esadecimale(_ dati: Data) -> String {
        dati.map { String(format: "%02x", $0) }.joined()
    }
}
