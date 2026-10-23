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

#include <obs-module.h>
#include <util/bmem.h>
#include <util/platform.h>
#include <util/threading.h>
#include <plugin-support.h>

#include "bacheca.h"

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE(PLUGIN_NAME, "en-US")

// Senza battito da più di così il ricevitore è chiuso (o bloccato): la sorgente si svuota.
#define SILENZIO_MASSIMO_NS 1000000000ULL
// Il ricevitore suona il campanello a ogni immagine; si ricontrolla comunque ogni tanto.
#define ATTESA_MS 10
// Se il ricevitore non c'è, si riprova ad aprire la bacheca ogni secondo (a passi brevi, per
// chiudersi subito se la sorgente viene tolta).
#define PASSI_ATTESA_RICEVITORE 10
#define PASSO_ATTESA_RICEVITORE_MS 100

struct sorgente_mirrorloop {
	obs_source_t *sorgente;
	pthread_t thread;
	bool thread_avviato;
	volatile bool fermati; // solo con os_atomic_*: lo scrive OBS, lo legge il thread
};

static const char *mirrorloop_nome(void *dati_tipo)
{
	UNUSED_PARAMETER(dati_tipo);
	return obs_module_text("MirrorLoop");
}

static void consegna(struct sorgente_mirrorloop *s, const struct bacheca_immagine *immagine)
{
	struct obs_source_frame fotogramma = {0};
	fotogramma.width = immagine->larghezza;
	fotogramma.height = immagine->altezza;
	fotogramma.format = VIDEO_FORMAT_NV12;
	fotogramma.data[0] = immagine->dati;
	fotogramma.data[1] = immagine->dati + (size_t)immagine->larghezza * immagine->altezza;
	fotogramma.linesize[0] = immagine->larghezza;
	fotogramma.linesize[1] = immagine->larghezza;
	fotogramma.full_range = false;
	video_format_get_parameters(VIDEO_CS_709, VIDEO_RANGE_PARTIAL, fotogramma.color_matrix,
				    fotogramma.color_range_min, fotogramma.color_range_max);
	// L'istante è quello di messa in onda nel ricevitore, già nel tempo di OBS.
	fotogramma.timestamp = immagine->istante_ns;
	// OBS copia i dati: la copia della bacheca si può riusare subito dopo.
	obs_source_output_video(s->sorgente, &fotogramma);
}

static void *mirrorloop_thread(void *dati)
{
	struct sorgente_mirrorloop *s = dati;
	os_set_thread_name("mirrorloop: lettura della bacheca");

	struct bacheca *bacheca = NULL;
	uint64_t ultima = 0;
	bool in_onda = false;

	while (!os_atomic_load_bool(&s->fermati)) {
		if (!bacheca) {
			bacheca = bacheca_apri();
			if (!bacheca) {
				for (int i = 0; i < PASSI_ATTESA_RICEVITORE && !os_atomic_load_bool(&s->fermati); i++)
					os_sleep_ms(PASSO_ATTESA_RICEVITORE_MS);
				continue;
			}
			obs_log(LOG_INFO, "ricevitore trovato");
			ultima = 0;
		}

		bacheca_aspetta(bacheca, ATTESA_MS);

		if (!bacheca_valida(bacheca)) {
			// Reinizializzata da un ricevitore di un'altra versione: si riapre da capo.
			obs_log(LOG_WARNING, "bacheca non più valida, la riapro");
			bacheca_chiudi(bacheca);
			bacheca = NULL;
			continue;
		}

		// Ricevitore chiuso o bloccato: niente immagine ferma in onda, la sorgente si svuota.
		bool vivo = os_gettime_ns() - bacheca_battito(bacheca) < SILENZIO_MASSIMO_NS;
		if (!vivo) {
			if (in_onda) {
				obs_source_output_video(s->sorgente, NULL);
				in_onda = false;
				obs_log(LOG_INFO, "ricevitore in silenzio: sorgente vuota");
			}
			continue;
		}

		struct bacheca_immagine immagine;
		if (bacheca_ultima_immagine(bacheca, ultima, &immagine)) {
			consegna(s, &immagine);
			ultima = immagine.numero;
			in_onda = true;
		}
	}

	bacheca_chiudi(bacheca);
	return NULL;
}

static void *mirrorloop_crea(obs_data_t *impostazioni, obs_source_t *sorgente)
{
	UNUSED_PARAMETER(impostazioni);

	struct sorgente_mirrorloop *s = bzalloc(sizeof(*s));
	s->sorgente = sorgente;
	// Le immagini arrivano già al loro momento (il ricevitore le sincronizza con l'audio): OBS
	// deve mostrare sempre la più recente, senza tenerne da parte.
	obs_source_set_async_unbuffered(sorgente, true);

	if (pthread_create(&s->thread, NULL, mirrorloop_thread, s) == 0)
		s->thread_avviato = true;
	else
		obs_log(LOG_ERROR, "impossibile avviare il thread di lettura");

	return s;
}

static void mirrorloop_distruggi(void *dati)
{
	struct sorgente_mirrorloop *s = dati;

	if (s->thread_avviato) {
		os_atomic_set_bool(&s->fermati, true);
		pthread_join(s->thread, NULL);
	}
	bfree(s);
}

static struct obs_source_info sorgente_mirrorloop_info = {
	.id = "mirrorloop_sorgente",
	.type = OBS_SOURCE_TYPE_INPUT,
	.output_flags = OBS_SOURCE_ASYNC_VIDEO | OBS_SOURCE_DO_NOT_DUPLICATE,
	.get_name = mirrorloop_nome,
	.create = mirrorloop_crea,
	.destroy = mirrorloop_distruggi,
	.icon_type = OBS_ICON_TYPE_CAMERA,
};

bool obs_module_load(void)
{
	obs_register_source(&sorgente_mirrorloop_info);
	obs_log(LOG_INFO, "plugin caricato (versione %s)", PLUGIN_VERSION);
	return true;
}

void obs_module_unload(void)
{
	obs_log(LOG_INFO, "plugin scaricato");
}
