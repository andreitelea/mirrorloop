namespace MirrorLoop.Ricevitore.Test;

public sealed class RegistroPacchettiTest : IDisposable
{
    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "MirrorLoopTest-" + Guid.NewGuid());

    public void Dispose() => Directory.Delete(_cartella, recursive: true);

    [Fact]
    public void ScriveIntestazioneERigheSoloQuandoEAttivo()
    {
        using var registro = new RegistroPacchetti(_cartella);
        registro.Scrivi('v', 1, 2, null, 3, true);   // spento: non scrive niente

        registro.Avvia();
        registro.Scrivi('v', 1_000_000, 1_050_000, 40_000, 1234, true);
        registro.Scrivi('a', 1_020_000, 1_060_000, null, 3840, false);
        registro.ScriviRiserva(new RiservaAudio.Evento(RiservaAudio.TipoEvento.Buco, 960, 11520, 960, 1920), 1_070_000);
        registro.ScriviRiserva(new RiservaAudio.Evento(RiservaAudio.TipoEvento.Taglio, 27840, 11520, 16320, 1920), 1_080_000);
        registro.ScriviRiserva(new RiservaAudio.Evento(RiservaAudio.TipoEvento.TaglioInRiempimento, 30720, 19200, 11520, 1920), 1_090_000);
        registro.Ferma();

        Assert.False(registro.Attivo);
        Assert.Equal(5, registro.Righe);
        Assert.Equal(
            [
                "tipo,istante_iphone_us,arrivo_pc_us,scarto_us,byte,keyframe,riserva_byte,obiettivo_byte,lettura_byte",
                "v,1000000,1050000,40000,1234,1,,,",
                "a,1020000,1060000,,3840,0,,,",
                "b,,1070000,,960,0,960,11520,1920",
                "t,,1080000,,16320,0,27840,11520,1920",
                "r,,1090000,,11520,0,30720,19200,1920",
            ],
            File.ReadAllLines(registro.Percorso!));
    }
}
