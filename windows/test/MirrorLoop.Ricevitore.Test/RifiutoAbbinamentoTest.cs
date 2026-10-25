namespace MirrorLoop.Ricevitore.Test;

public sealed class RifiutoAbbinamentoTest
{
    private static readonly DateTime Ora = new(2026, 10, 25, 17, 42, 5);

    [Fact]
    public void LaRigaHaOraNomeEMotivo() =>
        Assert.Equal("17:42:05 «iPhone di Andre»: nessuna risposta su questo PC (60 s)",
            new RifiutoAbbinamento(MotivoRifiuto.Scaduto, "iPhone di Andre", "60 s", Ora).Riga);

    [Fact]
    public void SenzaNomeSiParlaDiUnIphone() =>
        Assert.Equal("17:42:05 un iPhone: rifiutato da questo PC",
            new RifiutoAbbinamento(MotivoRifiuto.RifiutatoDalPC, null, null, Ora).Riga);

    [Fact]
    public void OgniMotivoHaUnaDescrizione()
    {
        foreach (MotivoRifiuto motivo in Enum.GetValues<MotivoRifiuto>())
            Assert.NotEqual(motivo.ToString(), new RifiutoAbbinamento(motivo, null, null, Ora).Descrizione);
    }
}
