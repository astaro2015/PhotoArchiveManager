using System.Globalization;
using System.Windows;

namespace PhotoArchiveManager;

public partial class CaptureDateEditorWindow : Window
{
    public DateTime CaptureDate { get; private set; }

    public CaptureDateEditorWindow(DateTime initialDate, string currentSource)
    {
        InitializeComponent();
        DateBox.SelectedDate = initialDate.Date;
        TimeBox.Text = initialDate.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        CurrentSourceText.Text = string.IsNullOrWhiteSpace(currentSource)
            ? "Текущий источник даты: неизвестен"
            : "Текущий источник даты: " + currentSource;
        Loaded += (_, _) => TimeBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!DateBox.SelectedDate.HasValue)
        {
            ErrorText.Text = "Выберите дату.";
            return;
        }

        var formats = new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };
        if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), formats, CultureInfo.InvariantCulture, out var time) || time.TotalDays >= 1)
        {
            ErrorText.Text = "Введите время в формате ЧЧ:ММ или ЧЧ:ММ:СС, например 14:35:00.";
            return;
        }

        CaptureDate = DateBox.SelectedDate.Value.Date + time;
        DialogResult = true;
    }
}
