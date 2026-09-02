using PhotoArchiveManager.Models;
using System.Windows;

namespace PhotoArchiveManager;

public partial class EventPickerWindow : Window
{
    public IReadOnlyList<EventGroupItem> Events { get; }
    public EventGroupItem? SelectedEvent { get; set; }
    public string EventName => NameBox.Text;

    public EventPickerWindow(IReadOnlyList<EventGroupItem> events, string initialName)
    {
        InitializeComponent();
        Events = events;
        SelectedEvent = events.FirstOrDefault();
        NameBox.Text = initialName ?? "";
        DataContext = this;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEvent is null)
        {
            MessageBox.Show(this, "Выберите событие.", "Объединить события", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
