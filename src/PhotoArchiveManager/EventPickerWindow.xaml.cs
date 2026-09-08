using PhotoArchiveManager.Models;
using System.Windows;

namespace PhotoArchiveManager;

public partial class EventPickerWindow : Window
{
    public IReadOnlyList<EventGroupItem> Events { get; }
    public EventGroupItem? SelectedEvent { get; set; }
    public EventPickerWindow(IReadOnlyList<EventGroupItem> events)
    {
        InitializeComponent();
        Events = events;
        SelectedEvent = events.FirstOrDefault();
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
