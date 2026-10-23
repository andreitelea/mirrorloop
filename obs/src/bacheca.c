/*
MirrorLoop · sorgente per OBS
Copyright (C) 2026 Andrei Telea

This program is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License along
with this program. If not, see <https://www.gnu.org/licenses/>
*/

#include "bacheca.h"

#include <string.h>
#include <util/bmem.h>

#ifndef _WIN32
#error "La bacheca usa la memoria condivisa di Windows: per ora il plugin esiste solo per Windows."
#endif

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define NOME_MEMORIA L"Local\\MirrorLoopOBS"
#define NOME_EVENTO L"Local\\MirrorLoopOBS.Nuovo"
#define VERSIONE 1

// Posizioni nell'intestazione.
#define POS_FIRMA 0
#define POS_VERSIONE 4
#define POS_DIMENSIONE 8
#define POS_BATTITO 16
#define POS_IMMAGINI_SCRITTE 32
#define POS_NUMERO_CASSETTI 48
#define POS_DIMENSIONE_CASSETTO 52
#define POS_NUMERO_BLOCCHI 56
#define POS_DIMENSIONE_BLOCCO 60
#define DIMENSIONE_INTESTAZIONE 4096

// Posizioni dentro un cassetto immagine.
#define POS_SEQUENZA 0
#define POS_NUMERO 8
#define POS_ISTANTE 16
#define POS_GENERAZIONE 24
#define POS_LARGHEZZA 32
#define POS_ALTEZZA 36
#define POS_PASSO_Y 40
#define POS_PASSO_UV 44
#define POS_FORMATO 48
#define POS_COLORE 52
#define POS_BYTE 56
#define INTESTAZIONE_ELEMENTO 64

#define FORMATO_NV12 1
#define COLORE_BT709_LIMITATO 1

struct bacheca {
	HANDLE memoria;
	HANDLE evento;
	const uint8_t *base;
	uint64_t dimensione;
	uint32_t numero_cassetti;
	uint32_t dimensione_cassetto;
	uint8_t *copia; // un cassetto intero: l'immagine si copia qui prima di darla a OBS
};

static uint32_t leggi32(const uint8_t *p)
{
	uint32_t v;
	memcpy(&v, p, sizeof(v)); // little-endian come il PC
	return v;
}

// I contatori e le sequenze li scrive un altro processo: letture "volatili", allineate a 8 byte
// (su x64 una lettura allineata di 64 bit è atomica).
static uint64_t leggi64_volatile(const uint8_t *p)
{
	return *(const volatile uint64_t *)p;
}

struct bacheca *bacheca_apri(void)
{
	HANDLE memoria = OpenFileMappingW(FILE_MAP_READ, FALSE, NOME_MEMORIA);
	if (!memoria)
		return NULL;

	const uint8_t *base = MapViewOfFile(memoria, FILE_MAP_READ, 0, 0, 0);
	if (!base) {
		CloseHandle(memoria);
		return NULL;
	}

	// Quanto è grande davvero la vista: non si legge mai oltre, qualunque cosa dica l'intestazione.
	MEMORY_BASIC_INFORMATION info;
	SIZE_T dimensione_vista = VirtualQuery(base, &info, sizeof(info)) ? info.RegionSize : 0;

	struct bacheca *b = NULL;
	if (dimensione_vista >= DIMENSIONE_INTESTAZIONE && memcmp(base + POS_FIRMA, "MLOB", 4) == 0 &&
	    leggi32(base + POS_VERSIONE) == VERSIONE) {
		uint64_t dimensione;
		memcpy(&dimensione, base + POS_DIMENSIONE, sizeof(dimensione));
		uint32_t numero_cassetti = leggi32(base + POS_NUMERO_CASSETTI);
		uint32_t dimensione_cassetto = leggi32(base + POS_DIMENSIONE_CASSETTO);
		uint64_t fine_cassetti = DIMENSIONE_INTESTAZIONE + (uint64_t)numero_cassetti * dimensione_cassetto;

		if (dimensione <= dimensione_vista && numero_cassetti > 0 && dimensione_cassetto > INTESTAZIONE_ELEMENTO &&
		    fine_cassetti <= dimensione) {
			HANDLE evento = OpenEventW(SYNCHRONIZE, FALSE, NOME_EVENTO);
			b = bzalloc(sizeof(*b));
			b->memoria = memoria;
			b->evento = evento; // può mancare: si ricontrolla comunque ogni pochi ms
			b->base = base;
			b->dimensione = dimensione;
			b->numero_cassetti = numero_cassetti;
			b->dimensione_cassetto = dimensione_cassetto;
			b->copia = bmalloc(dimensione_cassetto);
		}
	}

	if (!b) {
		UnmapViewOfFile(base);
		CloseHandle(memoria);
	}
	return b;
}

void bacheca_chiudi(struct bacheca *b)
{
	if (!b)
		return;
	if (b->evento)
		CloseHandle(b->evento);
	UnmapViewOfFile(b->base);
	CloseHandle(b->memoria);
	bfree(b->copia);
	bfree(b);
}

void bacheca_aspetta(struct bacheca *b, uint32_t millisecondi)
{
	if (b->evento)
		WaitForSingleObject(b->evento, millisecondi);
	else
		Sleep(millisecondi);
}

uint64_t bacheca_battito(const struct bacheca *b)
{
	return leggi64_volatile(b->base + POS_BATTITO);
}

bool bacheca_valida(const struct bacheca *b)
{
	return memcmp(b->base + POS_FIRMA, "MLOB", 4) == 0 && leggi32(b->base + POS_VERSIONE) == VERSIONE;
}

bool bacheca_ultima_immagine(struct bacheca *b, uint64_t dopo_numero, struct bacheca_immagine *immagine)
{
	uint64_t numero = leggi64_volatile(b->base + POS_IMMAGINI_SCRITTE);
	if (numero == 0 || numero == dopo_numero)
		return false;

	const uint8_t *cassetto =
		b->base + DIMENSIONE_INTESTAZIONE + ((numero - 1) % b->numero_cassetti) * (uint64_t)b->dimensione_cassetto;

	// Seqlock (vedi il documento): sequenza dispari = il ricevitore sta scrivendo.
	uint64_t sequenza = leggi64_volatile(cassetto + POS_SEQUENZA);
	MemoryBarrier();
	if (sequenza % 2 != 0 || leggi64_volatile(cassetto + POS_NUMERO) != numero)
		return false;

	uint32_t larghezza = leggi32(cassetto + POS_LARGHEZZA);
	uint32_t altezza = leggi32(cassetto + POS_ALTEZZA);
	uint32_t byte = leggi32(cassetto + POS_BYTE);
	bool valida = leggi32(cassetto + POS_FORMATO) == FORMATO_NV12 &&
		      leggi32(cassetto + POS_COLORE) == COLORE_BT709_LIMITATO && larghezza > 0 && altezza > 0 &&
		      larghezza % 2 == 0 && altezza % 2 == 0 && leggi32(cassetto + POS_PASSO_Y) == larghezza &&
		      leggi32(cassetto + POS_PASSO_UV) == larghezza &&
		      (uint64_t)larghezza * altezza * 3 / 2 == byte && byte <= b->dimensione_cassetto - INTESTAZIONE_ELEMENTO;
	if (!valida)
		return false;

	memcpy(b->copia, cassetto + INTESTAZIONE_ELEMENTO, byte);
	immagine->numero = numero;
	immagine->istante_ns = leggi64_volatile(cassetto + POS_ISTANTE);
	immagine->generazione = leggi64_volatile(cassetto + POS_GENERAZIONE);
	immagine->larghezza = larghezza;
	immagine->altezza = altezza;
	immagine->dati = b->copia;

	// Se nel frattempo il ricevitore ha riscritto il cassetto, la copia può essere mista.
	MemoryBarrier();
	return leggi64_volatile(cassetto + POS_SEQUENZA) == sequenza;
}
