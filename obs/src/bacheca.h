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

#pragma once

// Lettura della memoria condivisa scritta dal ricevitore MirrorLoop.
// Il formato è in docs/memoria-condivisa.md: ogni modifica va fatta lì, qui e nel ricevitore insieme.

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct bacheca;

// Un'immagine copiata dalla bacheca: NV12 con righe compatte (passo = larghezza).
struct bacheca_immagine {
	uint64_t numero;
	uint64_t istante_ns; // nel tempo di OBS (os_gettime_ns)
	uint64_t generazione;
	uint32_t larghezza;
	uint32_t altezza;
	uint8_t *dati; // di proprietà della bacheca, valido fino alla prossima lettura
};

// Apre la bacheca se il ricevitore l'ha creata; NULL se non c'è (o se ha un formato che non conosciamo).
struct bacheca *bacheca_apri(void);
void bacheca_chiudi(struct bacheca *b);

// Aspetta al massimo `millisecondi` che il ricevitore segnali una scrittura.
void bacheca_aspetta(struct bacheca *b, uint32_t millisecondi);

// Ultimo battito del ricevitore, nel tempo di OBS.
uint64_t bacheca_battito(const struct bacheca *b);

// Firma e versione ancora valide? (un ricevitore di un'altra versione può averla reinizializzata)
bool bacheca_valida(const struct bacheca *b);

// Copia l'ultima immagine se è più nuova di `dopo_numero`. false se non ce n'è una nuova o se
// la copia è risultata mista (il ricevitore la stava riscrivendo): basta riprovare.
bool bacheca_ultima_immagine(struct bacheca *b, uint64_t dopo_numero, struct bacheca_immagine *immagine);

#ifdef __cplusplus
}
#endif
