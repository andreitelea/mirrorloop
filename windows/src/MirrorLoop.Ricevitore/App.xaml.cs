using System.Globalization;
using System.Windows;

namespace MirrorLoop.Ricevitore;

public partial class App : Application
{
    /// <summary>
    /// La lingua dei testi è quella di Windows (italiano se Windows è in italiano, altrimenti inglese).
    /// "--lingua en" (o "it") la impone, per provare l'altra lingua senza cambiare quella di Windows.
    /// Si cambia solo la lingua dei testi: numeri e date restano nel formato regionale di Windows.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        int posizione = Array.IndexOf(e.Args, "--lingua");
        if (posizione >= 0 && posizione + 1 < e.Args.Length)
        {
            try
            {
                var lingua = CultureInfo.GetCultureInfo(e.Args[posizione + 1]);
                CultureInfo.DefaultThreadCurrentUICulture = lingua;
                CultureInfo.CurrentUICulture = lingua;
            }
            catch (CultureNotFoundException)
            {
                // Lingua sconosciuta: si resta su quella di Windows.
            }
        }
        base.OnStartup(e);
    }
}
