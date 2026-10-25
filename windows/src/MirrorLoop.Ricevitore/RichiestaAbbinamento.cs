namespace MirrorLoop.Ricevitore;

/// <summary>
/// Un iPhone nuovo chiede di collegarsi: chi sta al PC consente solo se sull'iPhone compare lo
/// stesso codice (docs/protocollo.md, "Abbinamento").
/// </summary>
/// <param name="Codice">6 cifre.</param>
/// <param name="Nome">Il nome mandato dal telefono (di solito solo "iPhone").</param>
public sealed record RichiestaAbbinamento(string Codice, string Nome)
{
    /// <summary>Il codice in due gruppi da tre, più facile da confrontare a colpo d'occhio.</summary>
    public string CodiceLeggibile => $"{Codice[..3]} {Codice[3..]}";
}
