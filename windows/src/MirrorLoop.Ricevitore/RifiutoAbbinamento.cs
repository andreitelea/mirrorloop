namespace MirrorLoop.Ricevitore;

/// <summary>Perché il ricevitore ha rifiutato un iPhone durante l'abbinamento.</summary>
public enum MotivoRifiuto
{
    /// <summary>Nessuna presentazione (tipo 6) entro il limite per messaggio.</summary>
    NessunaPresentazione,
    /// <summary>Nessuna prova (tipo 8) entro il limite per messaggio.</summary>
    NessunaProva,
    /// <summary>Impegno o firma che non tornano: un impostore in mezzo, o un errore dell'app.</summary>
    VerificaFallita,
    /// <summary>Si aspettava già la conferma per un altro telefono.</summary>
    AltraRichiestaInCorso,
    /// <summary>Rifiuta o Esc nella finestra.</summary>
    RifiutatoDalPC,
    /// <summary>Nessuna risposta nella finestra entro l'attesa della conferma.</summary>
    Scaduto,
    /// <summary>Connessione chiusa dal telefono ("Annulla" sull'iPhone) o persa.</summary>
    TelefonoScollegato,
    /// <summary>Un messaggio malformato o di un tipo che lì non c'entra.</summary>
    MessaggioNonValido,
    /// <summary>Ricevitore senza nessuno a cui chiedere il consenso (solo nei test).</summary>
    NuoviTelefoniNonAmmessi,
}

/// <summary>
/// Un rifiuto, da mostrare a chi sta al PC: all'iPhone arriva solo "rifiutato", ma qui si vuole
/// sapere il perché (per esempio un telefono che non risponde in tempo).
/// </summary>
/// <param name="NomeTelefono">Il nome mandato dal telefono, se si è presentato.</param>
/// <param name="Dettaglio">Un'informazione in più (il limite di tempo, il messaggio tecnico).</param>
public sealed record RifiutoAbbinamento(MotivoRifiuto Motivo, string? NomeTelefono, string? Dettaglio, DateTime Ora)
{
    public string Descrizione
    {
        get
        {
            string motivo = Motivo switch
            {
                MotivoRifiuto.NessunaPresentazione => "l'iPhone non si è presentato in tempo",
                MotivoRifiuto.NessunaProva => "l'iPhone non ha risposto in tempo durante la verifica",
                MotivoRifiuto.VerificaFallita => "verifica di sicurezza non riuscita (chiave o firma non valide)",
                MotivoRifiuto.AltraRichiestaInCorso => "c'era già un'altra richiesta di abbinamento in corso",
                MotivoRifiuto.RifiutatoDalPC => "rifiutato da questo PC",
                MotivoRifiuto.Scaduto => "nessuna risposta su questo PC",
                MotivoRifiuto.TelefonoScollegato => "l'iPhone ha annullato o si è scollegato",
                MotivoRifiuto.MessaggioNonValido => "messaggio non valido dall'iPhone",
                MotivoRifiuto.NuoviTelefoniNonAmmessi => "questo ricevitore non accetta telefoni nuovi",
                _ => Motivo.ToString(),
            };
            return Dettaglio is null ? motivo : $"{motivo} ({Dettaglio})";
        }
    }

    /// <summary>Una riga per la finestra: ora, telefono e motivo.</summary>
    public string Riga => $"{Ora:HH:mm:ss} {(NomeTelefono is null ? "un iPhone" : $"«{NomeTelefono}»")}: {Descrizione}";
}
