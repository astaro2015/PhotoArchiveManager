using PhotoArchiveManager.Models;
using System.Windows;

namespace PhotoArchiveManager;

public partial class PersonPickerWindow : Window
{
    public IReadOnlyList<PersonGroupItem> Groups { get; }
    public string Prompt { get; }
    public PersonGroupItem? SelectedGroup { get; set; }

    public PersonPickerWindow(string prompt, IReadOnlyList<PersonGroupItem> groups)
    {
        InitializeComponent();
        Prompt = prompt;
        Groups = groups;
        SelectedGroup = groups.FirstOrDefault();
        DataContext = this;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is null)
        {
            MessageBox.Show(this, "Выберите группу человека.", "Выбор человека", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
