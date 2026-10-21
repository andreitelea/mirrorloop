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
        registro.Ferma();

        Assert.False(registro.Attivo);
        Assert.Equal(2, registro.Righe);
        Assert.Equal(
            [
                "tipo,istante_iphone_us,arrivo_pc_us,scarto_us,byte,keyframe",
                "v,1000000,1050000,40000,1234,1",
                "a,1020000,1060000,,3840,0",
            ],
            File.ReadAllLines(registro.Percorso!));
    }
}
