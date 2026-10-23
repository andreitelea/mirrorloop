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

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE(PLUGIN_NAME, "en-US")

// Per ora un'immagine di prova: stesse misure e stesso formato (NV12) delle
// immagini che arriveranno dal ricevitore, così questa prova percorre già la
// strada vera (fotogrammi asincroni consegnati a OBS da un thread nostro).
#define LARGHEZZA 1088
#define ALTEZZA 2352
#define FPS 30
#define ALTEZZA_BARRA 120
#define PASSO_BARRA 26

struct sorgente_mirrorloop {
	obs_source_t *sorgente;
	pthread_t thread;
	bool thread_avviato;
	volatile bool fermati; // solo con os_atomic_*: lo scrive OBS, lo legge il thread
	uint8_t *nv12;
};

static const char *mirrorloop_nome(void *dati_tipo)
{
	UNUSED_PARAMETER(dati_tipo);
	return obs_module_text("MirrorLoop");
}

static void disegna_prova(uint8_t *nv12, int inizio_barra)
{
	uint8_t *y = nv12;
	uint8_t *uv = nv12 + LARGHEZZA * ALTEZZA;

	for (int riga = 0; riga < ALTEZZA; riga++) {
		bool in_barra = riga >= inizio_barra && riga < inizio_barra + ALTEZZA_BARRA;
		// Gamma limitata (16-235), come il video H.264 dell'iPhone.
		memset(y + (size_t)riga * LARGHEZZA, in_barra ? 235 : 40, LARGHEZZA);
	}
	// Croma neutra: immagine in scala di grigi.
	memset(uv, 128, (size_t)LARGHEZZA * ALTEZZA / 2);
}

static void *mirrorloop_thread(void *dati)
{
	struct sorgente_mirrorloop *s = dati;
	os_set_thread_name("mirrorloop: immagine di prova");

	struct obs_source_frame fotogramma = {0};
	fotogramma.width = LARGHEZZA;
	fotogramma.height = ALTEZZA;
	fotogramma.format = VIDEO_FORMAT_NV12;
	fotogramma.data[0] = s->nv12;
	fotogramma.data[1] = s->nv12 + LARGHEZZA * ALTEZZA;
	fotogramma.linesize[0] = LARGHEZZA;
	fotogramma.linesize[1] = LARGHEZZA;
	fotogramma.full_range = false;
	video_format_get_parameters(VIDEO_CS_709, VIDEO_RANGE_PARTIAL, fotogramma.color_matrix,
				    fotogramma.color_range_min, fotogramma.color_range_max);

	const uint64_t intervallo = 1000000000ULL / FPS;
	uint64_t prossimo = os_gettime_ns();
	int inizio_barra = 0;

	while (!os_atomic_load_bool(&s->fermati)) {
		disegna_prova(s->nv12, inizio_barra);
		inizio_barra = (inizio_barra + PASSO_BARRA) % ALTEZZA;

		fotogramma.timestamp = os_gettime_ns();
		// OBS copia i dati: il buffer si può riusare subito dopo.
		obs_source_output_video(s->sorgente, &fotogramma);

		prossimo += intervallo;
		os_sleepto_ns(prossimo);
	}
	return NULL;
}

static void *mirrorloop_crea(obs_data_t *impostazioni, obs_source_t *sorgente)
{
	UNUSED_PARAMETER(impostazioni);

	struct sorgente_mirrorloop *s = bzalloc(sizeof(*s));
	s->sorgente = sorgente;
	s->nv12 = bmalloc((size_t)LARGHEZZA * ALTEZZA * 3 / 2);

	if (pthread_create(&s->thread, NULL, mirrorloop_thread, s) == 0)
		s->thread_avviato = true;
	else
		obs_log(LOG_ERROR, "impossibile avviare il thread dell'immagine di prova");

	return s;
}

static void mirrorloop_distruggi(void *dati)
{
	struct sorgente_mirrorloop *s = dati;

	if (s->thread_avviato) {
		os_atomic_set_bool(&s->fermati, true);
		pthread_join(s->thread, NULL);
	}
	bfree(s->nv12);
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
