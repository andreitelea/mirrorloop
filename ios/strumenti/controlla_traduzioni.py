"""Controlla che ogni testo da tradurre dell'app abbia la traduzione italiana.

Il compilatore Swift, con SWIFT_EMIT_LOC_STRINGS, scrive per ogni file un .stringsdata con le
chiavi esatte dei testi (Text("…"), String(localized: "…"), con i segnaposto %@, %lld… già al
loro posto). Questo script le confronta con Localizable.xcstrings: una chiave che manca nel
catalogo, o senza italiano, comparirebbe in inglese anche a chi ha l'iPhone in italiano.

Uso: python3 controlla_traduzioni.py <cartella della compilazione> <Localizable.xcstrings>
"""
import json
import plistlib
import sys
from pathlib import Path


def leggi_stringsdata(percorso):
    dati = percorso.read_bytes()
    try:
        return json.loads(dati)
    except ValueError:
        return plistlib.loads(dati)


def main():
    cartella, catalogo = Path(sys.argv[1]), Path(sys.argv[2])
    file = sorted(cartella.rglob("*.stringsdata"))
    if not file:
        sys.exit("Nessun file .stringsdata: il compilatore non ha elencato i testi (SWIFT_EMIT_LOC_STRINGS?)")

    chiavi = {}
    for percorso in file:
        for voce in leggi_stringsdata(percorso).get("tables", {}).get("Localizable", []):
            chiavi.setdefault(voce["key"], percorso.stem)

    testi = json.loads(catalogo.read_text(encoding="utf-8"))["strings"]
    mancanti = []
    for chiave, origine in sorted(chiavi.items()):
        unita = testi.get(chiave, {}).get("localizations", {}).get("it", {}).get("stringUnit", {})
        if unita.get("state") != "translated" or not unita.get("value"):
            mancanti.append((origine, chiave))
    inutili = sorted(set(testi) - set(chiavi))

    print(f"{len(chiavi)} testi nel codice, {len(testi)} nel catalogo")
    for chiave in inutili:
        print(f"Nel catalogo ma non nel codice (da togliere?): {chiave!r}")
    for origine, chiave in mancanti:
        print(f"::error::Senza traduzione italiana ({origine}): {chiave!r}")
    if mancanti:
        sys.exit(1)


main()
