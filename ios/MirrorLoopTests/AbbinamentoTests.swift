import CryptoKit
import XCTest

/// I calcoli dell'abbinamento devono dare esattamente l'esempio di docs/protocollo.md, lo stesso
/// che verifica il ricevitore in C# (AbbinamentoTest.LEsempioDelDocumentoTorna).
final class AbbinamentoTests: XCTestCase {
    private let privataEsempio = Data(SHA256.hash(data: Data("MirrorLoop chiave di prova".utf8)))
    private let improntaEsempio = Data(SHA256.hash(data: Data("certificato di prova".utf8)))
    private let numeroPcEsempio = Data(repeating: 0x11, count: 32)
    private let numeroIphoneEsempio = Data(repeating: 0x22, count: 32)

    private func esadecimale(_ dati: Data) -> String {
        dati.map { String(format: "%02x", $0) }.joined()
    }

    private func dati(_ esadecimale: String) -> Data {
        var risultato = Data()
        var indice = esadecimale.startIndex
        while indice < esadecimale.endIndex {
            let prossimo = esadecimale.index(indice, offsetBy: 2)
            risultato.append(UInt8(esadecimale[indice..<prossimo], radix: 16)!)
            indice = prossimo
        }
        return risultato
    }

    func testLEsempioDelDocumentoTorna() throws {
        let chiave = ChiaveSoftware(privata: try P256.Signing.PrivateKey(rawRepresentation: privataEsempio))

        XCTAssertEqual(esadecimale(chiave.pubblica),
                       "0402e1ecbb59b6a867788ad406194e9d186d62a8ea3ae31b441e5df2cdbeb5074647f2ee5bcdd4948f2748373ace3a10fa44233730c2d4353437edbf6ae98504f5")
        XCTAssertEqual(esadecimale(improntaEsempio), "45bd8dfef7caf4232b2c31bfc249e6a5dd1743c727291408d2f0a05ebe0db347")
        XCTAssertEqual(esadecimale(Abbinamento.impegno(chiave: chiave.pubblica, numeroIphone: numeroIphoneEsempio)),
                       "ba04f4ce2586799326edcb32db1e6e33dfc33e22534f243e167387de0f8e6c5e")
        XCTAssertEqual(Abbinamento.codice(impronta: improntaEsempio, chiave: chiave.pubblica,
                                          numeroPc: numeroPcEsempio, numeroIphone: numeroIphoneEsempio), "713021")

        // La firma del documento, fatta in C# sul PC: deve risultare valida anche qui.
        let firmaDelDocumento = dati("e754f9bd671aaa7774779c04e57639fea1c9080b5d966ebc5a39641d7e607eb4be622936d4a8e52527dddbe66b7da6fd7d4817d30f047b6a62521dfc8f5191f9")
        XCTAssertTrue(Abbinamento.firmaValida(chiave: chiave.pubblica, impronta: improntaEsempio,
                                              numeroPc: numeroPcEsempio, numeroIphone: numeroIphoneEsempio, firma: firmaDelDocumento))
    }

    func testUnaFirmaDellAppEValidaSoloPerIDatiFirmati() throws {
        let chiave = ChiaveSoftware(privata: P256.Signing.PrivateKey())
        let numeroPc = Abbinamento.nuovoNumero(), numeroIphone = Abbinamento.nuovoNumero()
        let firma = try chiave.firma(Abbinamento.datiDaFirmare(impronta: improntaEsempio, chiave: chiave.pubblica,
                                                                numeroPc: numeroPc, numeroIphone: numeroIphone))

        XCTAssertEqual(firma.count, 64)
        XCTAssertTrue(Abbinamento.firmaValida(chiave: chiave.pubblica, impronta: improntaEsempio,
                                              numeroPc: numeroPc, numeroIphone: numeroIphone, firma: firma))
        // Su un'altra impronta (un impostore in mezzo) o con i numeri scambiati non vale.
        XCTAssertFalse(Abbinamento.firmaValida(chiave: chiave.pubblica, impronta: Abbinamento.nuovoNumero(),
                                               numeroPc: numeroPc, numeroIphone: numeroIphone, firma: firma))
        XCTAssertFalse(Abbinamento.firmaValida(chiave: chiave.pubblica, impronta: improntaEsempio,
                                               numeroPc: numeroIphone, numeroIphone: numeroPc, firma: firma))
    }

    func testIlCodiceHaSempreSeiCifre() {
        for _ in 0..<500 {
            let codice = Abbinamento.codice(impronta: improntaEsempio, chiave: Data(count: 65),
                                            numeroPc: Abbinamento.nuovoNumero(), numeroIphone: Abbinamento.nuovoNumero())
            XCTAssertEqual(codice.count, 6)
            XCTAssertTrue(codice.allSatisfy(\.isASCIIDigit), codice)
        }
        XCTAssertEqual(Abbinamento.leggibile("012345"), "012 345")
    }

    func testIMessaggiHannoIlFormatoDelDocumento() {
        let chiave = Data((0..<65).map { UInt8($0) })
        let impegno = Data(repeating: 0xAA, count: 32)
        let presentazione = Abbinamento.messaggioPresentazione(modo: .nuovoAbbinamento, chiave: chiave, impegno: impegno, nome: "Né")

        // Lunghezza = tipo + modo + chiave + impegno + "Né" in UTF-8 (3 byte), come nel test C#.
        XCTAssertEqual(Array(presentazione.prefix(6)), [0, 0, 0, 1 + 1 + 65 + 32 + 3, 6, 1])
        XCTAssertEqual(presentazione.subdata(in: 6..<71), chiave)
        XCTAssertEqual(presentazione.subdata(in: 71..<103), impegno)
        XCTAssertEqual(Array(presentazione.suffix(3)), [UInt8(ascii: "N"), 0xC3, 0xA9])

        let prova = Abbinamento.messaggioProva(numeroIphone: Data(repeating: 1, count: 32), firma: Data(repeating: 2, count: 64))
        XCTAssertEqual(prova.count, 4 + 97)
        XCTAssertEqual(Array(prova.prefix(5)), [0, 0, 0, 97, 8])
    }
}

private extension Character {
    var isASCIIDigit: Bool { isASCII && isNumber }
}
