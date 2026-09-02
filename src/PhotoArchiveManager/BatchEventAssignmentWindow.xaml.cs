using PhotoArchiveManager.Models;
using System.Windows;

namespace PhotoArchiveManager;

public partial class BatchEventAssignmentWindow : Window
{
    public IReadOnlyList<EventGroupItem> Events { get; }
    public EventGroupItem? SelectedEvent { get; set; }

    public BatchEventAssignmentWindow(int count, IReadOnlyList<EventGroupItem> events)
    {
        InitializeComponent();
        Events = events;
        SelectedEvent = events.FirstOrDefault();
        TitleText.Text = $"Назначить событие для {count:N0} фото";
        DataContext = this;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEvent is null)
        {
            MessageBox.Show(this, "Выберите событие.", "Назначить событие", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
