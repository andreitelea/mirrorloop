using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MirrorLoop.Ricevitore;

/// <summary>
/// Chiaro o scuro: la finestra principale è sempre scura (dietro al video serve il nero), le
/// finestrelle seguono la modalità di Windows (Impostazioni → Personalizzazione → Colori).
/// </summary>
internal static partial class Tema
{
    private const int DwmwaUseImmersiveDarkMode = 20; // dwmapi.h, Windows SDK 10.0.26100

    /// <summary>
    /// Vero se le app di Windows sono in modalità chiara. È il valore AppsUseLightTheme che scrive la
    /// pagina Colori di Impostazioni (non documentato da Microsoft, ma è quello che leggono tutti);
    /// senza, Windows è chiaro.
    /// </summary>
    public static bool WindowsInChiaro()
    {
        using RegistryKey? chiave = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return chiave?.GetValue("AppsUseLightTheme") is not int valore || valore != 0;
    }

    /// <summary>Mette alla finestra i colori del tema indicato e la barra del titolo coerente.</summary>
    public static void Applica(Window finestra, bool chiaro)
    {
        string nome = chiaro ? "TemaChiaro" : "TemaScuro";
        finestra.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/MirrorLoop.Ricevitore;component/Aspetto/{nome}.xaml"),
        });
        // La barra del titolo la disegna Windows: chiara se non glielo si chiede.
        if (!chiaro)
            finestra.SourceInitialized += (_, _) => BarraScura(finestra);
    }

    private static void BarraScura(Window finestra)
    {
        int sì = 1;
        // Su Windows più vecchi l'attributo non esiste: la barra resta chiara, nient'altro.
        _ = DwmSetWindowAttribute(new WindowInteropHelper(finestra).Handle, DwmwaUseImmersiveDarkMode, ref sì, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attributo, ref int valore, int dimensione);
}
