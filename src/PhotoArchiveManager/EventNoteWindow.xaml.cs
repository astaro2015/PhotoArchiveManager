using System.Windows;

namespace PhotoArchiveManager;

public partial class EventNoteWindow : Window
{
    public string Notes => NoteBox.Text.Trim();

    public EventNoteWindow(string? notes)
    {
        InitializeComponent();
        NoteBox.Text = notes ?? "";
        Loaded += (_, _) => { NoteBox.Focus(); NoteBox.CaretIndex = NoteBox.Text.Length; };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
