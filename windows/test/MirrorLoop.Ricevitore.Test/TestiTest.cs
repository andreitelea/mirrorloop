using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MirrorLoop.Ricevitore.Test;

/// <summary>
/// I test che confrontano i testi li aspettano in italiano: valgono qualunque sia la lingua del
/// Windows che li esegue. Chi prova l'inglese la cambia solo per sé (<see cref="InLingua"/>).
/// </summary>
internal static class LinguaDeiTest
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Assembly di test: deve valere prima di ogni test.")]
    internal static void Italiano()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("it-IT");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it-IT");
        // Anche i numeri come in Italia (virgola decimale), qualunque sia il formato del PC.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("it-IT");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
    }

    /// <summary>Esegue <paramref name="azione"/> con i testi nella lingua indicata, poi torna all'italiano.</summary>
    internal static T InLingua<T>(string lingua, Func<T> azione)
    {
        CultureInfo prima = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(lingua);
        try
        {
            return azione();
        }
        finally
        {
            CultureInfo.CurrentUICulture = prima;
        }
    }
}

public sealed partial class TestiTest
{
    private static Dictionary<string, string> Testi(CultureInfo lingua)
    {
        // Solo i testi di quella lingua, senza ricadere su quella di riserva.
        ResourceSet insieme = Ricevitore.Testi.ResourceManager.GetResourceSet(lingua, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"Nessun testo per {lingua.Name}");
        return insieme.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    [GeneratedRegex(@"\{(\d+)")]
    private static partial Regex Segnaposto();

    private static string[] Segnaposti(string testo) =>
        Segnaposto().Matches(testo).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void OgniTestoEsisteInTutteEDueLeLingueConGliStessiSegnaposti()
    {
        var inglese = Testi(CultureInfo.InvariantCulture);
        var italiano = Testi(CultureInfo.GetCultureInfo("it"));

        Assert.True(inglese.Count > 100, "il file inglese sembra vuoto");
        Assert.Equal(inglese.Keys.Order(), italiano.Keys.Order());
        foreach (var (nome, testo) in inglese)
        {
            Assert.False(string.IsNullOrWhiteSpace(italiano[nome]), nome);
            // "{0}" dimenticato in una traduzione: il valore sparirebbe dalla finestra.
            Assert.True(Segnaposti(testo).SequenceEqual(Segnaposti(italiano[nome])), nome);
        }
    }

    [Fact]
    public void LeEtichetteDelleStatisticheStannoIn15Caratteri()
    {
        foreach (var lingua in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("it") })
            foreach (var (nome, testo) in Testi(lingua).Where(t => t.Key.StartsWith("Stat") && !t.Key.StartsWith("Stato") && t.Key != "StatTasti"))
                Assert.True(testo.Length <= 15, $"{nome} in {lingua.Name}: «{testo}»");
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ChiNonHaWindowsInItalianoVedeLInglese(string lingua)
    {
        var rifiuto = new RifiutoAbbinamento(MotivoRifiuto.Scaduto, "Andrei's iPhone", "60 s", new DateTime(2026, 10, 25, 17, 42, 5));

        Assert.Equal("17:42:05 “Andrei's iPhone”: no answer on this PC (60 s)", LinguaDeiTest.InLingua(lingua, () => rifiuto.Riga));
        Assert.Equal("Andrei's PC", LinguaDeiTest.InLingua(lingua, () => Impostazioni.NomePredefinito("Andrei Telea", "Andrei", "DESKTOP")));
    }

    [Fact]
    public void ConWindowsInItalianoSiVedeLItaliano()
    {
        var rifiuto = new RifiutoAbbinamento(MotivoRifiuto.Scaduto, "iPhone di Andre", "60 s", new DateTime(2026, 10, 25, 17, 42, 5));

        Assert.Equal("17:42:05 «iPhone di Andre»: nessuna risposta su questo PC (60 s)", LinguaDeiTest.InLingua("it-IT", () => rifiuto.Riga));
        Assert.Equal("PC di Andrei", LinguaDeiTest.InLingua("it-CH", () => Impostazioni.NomePredefinito("Andrei Telea", "Andrei", "DESKTOP")));
    }
}
