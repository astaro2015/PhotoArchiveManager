using System.Windows;

namespace PhotoArchiveManager;

public partial class EventNameWindow : Window
{
    public string EventName => NameBox.Text;

    public EventNameWindow(string initialName)
    {
        InitializeComponent();
        NameBox.Text = initialName ?? "";
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
