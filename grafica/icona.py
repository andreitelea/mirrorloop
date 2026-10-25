"""Disegna l'icona di MirrorLoop in tutte le versioni che servono, senza librerie esterne.

L'icona: sfondo sfumato dal blu al viola, un anello bianco e un punto che "gira" sull'anello,
staccato da un bordo del colore di sfondo. Tutto è geometria (cerchi e sfumature), quindi si
disegna a qualsiasi misura calcolando per ogni pixel quanto è coperto dalle forme.

Uso, dalla cartella principale del repository:  python grafica/icona.py
Scrive:
  ios/MirrorLoop/Assets.xcassets/AppIcon.appiconset/AppIcon.png        icona dell'app (1024, opaca)
  ios/MirrorLoop/Assets.xcassets/AppIcon.appiconset/AppIcon-scura.png  variante scura: sfondo
      trasparente, lo mette iOS (documentazione Apple "Configuring your app icon")
  ios/MirrorLoop/Assets.xcassets/AppIcon.appiconset/AppIcon-tinta.png  variante tinta: in scala
      di grigi, iOS la colora con la tinta scelta da chi usa l'iPhone
  windows/src/MirrorLoop.Ricevitore/MirrorLoop.ico                     ricevitore: da 16 a 256 pixel,
      quadrato con gli angoli arrotondati e trasparenti
"""
import math
import struct
import zlib
from pathlib import Path

RADICE = Path(__file__).resolve().parent.parent
ICONE_IOS = RADICE / "ios/MirrorLoop/Assets.xcassets/AppIcon.appiconset"
ICONA_WINDOWS = RADICE / "windows/src/MirrorLoop.Ricevitore/MirrorLoop.ico"

# Le misure sono riferite a un'icona di 1024 pixel e si scalano con la misura richiesta.
BLU = (0x1E, 0x3A, 0x8A)
VIOLA = (0x7C, 0x3A, 0xED)
RAGGIO, SPESSORE = 300.0, 72.0      # anello
RAGGIO_PUNTO, BORDO_PUNTO = 92.0, 26.0
ANGOLO_PUNTO = math.radians(-45)    # in alto a destra
ANGOLI_WINDOWS = 0.2                # raggio degli angoli, in proporzione al lato
MISURE_WINDOWS = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def copertura(distanza_dal_bordo):
    """Quanto un pixel è coperto da una forma, dalla distanza (in pixel) del suo centro dal bordo:
    positiva dentro. Mezzo pixel di sfumatura per lato: bordi lisci a ogni misura."""
    return min(1.0, max(0.0, distanza_dal_bordo + 0.5))


def mescola(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(len(a)))


def disegna(n, variante="normale", angoli=0.0):
    """Restituisce le righe di pixel RGBA (valori 0–255 come float) di un'icona n×n."""
    s = n / 1024
    c = n / 2
    raggio, spessore = RAGGIO * s, SPESSORE * s
    punto_x = c + raggio * math.cos(ANGOLO_PUNTO)
    punto_y = c + raggio * math.sin(ANGOLO_PUNTO)
    raggio_punto, bordo_punto = RAGGIO_PUNTO * s, BORDO_PUNTO * s
    raggio_angoli = angoli * n
    # Variante scura: anello e punto con i colori dello sfondo, schiariti perché si vedano sul
    # fondo scuro di iOS.
    blu_chiaro, viola_chiaro = mescola(BLU, (255, 255, 255), 0.35), mescola(VIOLA, (255, 255, 255), 0.35)

    righe = []
    for y in range(n):
        riga = []
        for x in range(n):
            px, py = x + 0.5, y + 0.5
            t = (px + py) / (2 * n)
            d = math.hypot(px - c, py - c)
            anello = copertura(spessore / 2 - abs(d - raggio))
            d_punto = math.hypot(px - punto_x, py - punto_y)
            stacco = copertura(raggio_punto + bordo_punto - d_punto)
            punto = copertura(raggio_punto - d_punto)
            disegno = max(anello * (1 - stacco), punto)

            # Il quadrato con gli angoli arrotondati (solo per Windows: su iOS li fa il sistema).
            forma = 1.0
            if raggio_angoli > 0:
                qx = max(abs(px - c) - (c - raggio_angoli), 0.0)
                qy = max(abs(py - c) - (c - raggio_angoli), 0.0)
                forma = copertura(raggio_angoli - math.hypot(qx, qy))

            if variante == "normale":
                fondo = mescola(BLU, VIOLA, t)
                colore = mescola(fondo, (255, 255, 255), disegno)
                riga.append((*colore, 255 * forma))
            elif variante == "scura":
                riga.append((*mescola(blu_chiaro, viola_chiaro, t), 255 * disegno))
            else:  # tinta: bianco su nero, iOS sostituisce il bianco con la tinta
                grigio = 255 * disegno
                riga.append((grigio, grigio, grigio, 255))
        righe.append(riga)
    return righe


def png(righe, tipo):
    """PNG minimo: tipo "RGB", "RGBA" o "grigio"."""
    n = len(righe)
    canali = {"RGB": (2, 3), "RGBA": (6, 4), "grigio": (0, 1)}[tipo]
    dati = bytearray()
    for riga in righe:
        dati.append(0)
        for pixel in riga:
            dati += bytes(int(round(v)) for v in pixel[:canali[1]])

    def blocco(etichetta, contenuto):
        return (struct.pack(">I", len(contenuto)) + etichetta + contenuto
                + struct.pack(">I", zlib.crc32(etichetta + contenuto) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + blocco(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, canali[0], 0, 0, 0))
            + blocco(b"IDAT", zlib.compress(bytes(dati), 9))
            + blocco(b"IEND", b""))


def ico(immagini):
    """File .ico con un PNG per misura (formato accettato da Windows Vista in poi)."""
    intestazione = struct.pack("<HHH", 0, 1, len(immagini))
    voci, dati = b"", b""
    posizione = 6 + 16 * len(immagini)
    for lato, contenuto in immagini:
        voci += struct.pack("<BBBBHHII", lato % 256, lato % 256, 0, 0, 1, 32, len(contenuto), posizione + len(dati))
        dati += contenuto
    return intestazione + voci + dati


def scrivi(percorso, contenuto):
    percorso.write_bytes(contenuto)
    print(f"{percorso.relative_to(RADICE)}  ({len(contenuto)} byte)")


def main():
    scrivi(ICONE_IOS / "AppIcon.png", png(disegna(1024), "RGB"))
    scrivi(ICONE_IOS / "AppIcon-scura.png", png(disegna(1024, "scura"), "RGBA"))
    scrivi(ICONE_IOS / "AppIcon-tinta.png", png(disegna(1024, "tinta"), "grigio"))
    scrivi(ICONA_WINDOWS, ico([(lato, png(disegna(lato, angoli=ANGOLI_WINDOWS), "RGBA")) for lato in MISURE_WINDOWS]))


main()
