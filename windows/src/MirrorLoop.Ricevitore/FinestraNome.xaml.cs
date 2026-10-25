using System.Windows;

namespace MirrorLoop.Ricevitore;

/// <summary>Il nome con cui il PC compare sull'iPhone (tasto N). Chi la apre salva <see cref="NomeScelto"/>.</summary>
public partial class FinestraNome : Window
{
    private readonly string _predefinito;

    public FinestraNome(string attuale, string predefinito)
    {
        InitializeComponent();
        _predefinito = predefinito;
        CampoNome.Text = attuale;
        CampoNome.SelectAll();
        TestoPredefinito.Text = $"Predefinito: {predefinito}";
    }

    public string NomeScelto => CampoNome.Text;

    private void SuPredefinito(object sender, RoutedEventArgs e)
    {
        CampoNome.Text = _predefinito;
        CampoNome.Focus();
        CampoNome.SelectAll();
    }

    private void SuSalva(object sender, RoutedEventArgs e) => DialogResult = true;
}
