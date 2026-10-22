using MirrorLoop.Protocollo;

namespace MirrorLoop.Ricevitore.Test;

public class SceltaRotazioneTest
{
    [Fact]
    public void AllAvvioSegueLIPhone()
    {
        var scelta = new SceltaRotazione();
        Assert.Equal(RotazioneVideo.Nessuna, scelta.Effettiva);

        scelta.DaIPhone(RotazioneVideo.Gradi270);

        Assert.False(scelta.Manuale);
        Assert.Equal(RotazioneVideo.Gradi270, scelta.Effettiva);
    }

    [Fact]
    public void RuotareAManoParteDaQuelloCheSiVedeEGiraDi90()
    {
        var scelta = new SceltaRotazione();
        scelta.DaIPhone(RotazioneVideo.Gradi90);

        scelta.RuotaManuale();
        Assert.True(scelta.Manuale);
        Assert.Equal(RotazioneVideo.Gradi180, scelta.Effettiva);

        scelta.RuotaManuale();
        scelta.RuotaManuale();
        Assert.Equal(RotazioneVideo.Nessuna, scelta.Effettiva);   // dopo 270° si torna a 0°
    }

    [Fact]
    public void InManualeLIPhoneVieneIgnoratoFinoAlRitornoAllAutomatica()
    {
        var scelta = new SceltaRotazione();
        scelta.RuotaManuale();                     // 90° a mano

        scelta.DaIPhone(RotazioneVideo.Gradi270);
        Assert.Equal(RotazioneVideo.Gradi90, scelta.Effettiva);

        scelta.TornaAutomatica();
        Assert.False(scelta.Manuale);
        Assert.Equal(RotazioneVideo.Gradi270, scelta.Effettiva);
    }
}
