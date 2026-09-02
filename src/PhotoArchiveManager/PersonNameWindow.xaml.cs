using System.Windows;

namespace PhotoArchiveManager;

public partial class PersonNameWindow : Window
{
    public string PersonName => NameBox.Text;

    public PersonNameWindow(string initialName)
    {
        InitializeComponent();
        NameBox.Text = initialName ?? "";
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
