using System.Globalization;
using System.Windows;

namespace PhotoArchiveManager;

public partial class BatchCaptureDateWindow : Window
{
    public DateTime CaptureDate { get; private set; }
    public bool PreserveExistingTime => PreserveTimeBox.IsChecked == true;

    public BatchCaptureDateWindow(int count, DateTime initialDate)
    {
        InitializeComponent();
        TitleText.Text = $"Изменить дату у {count:N0} выбранных фото";
        DateBox.SelectedDate = initialDate.Date;
        TimeBox.Text = initialDate.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        UpdateTimeState();
    }

    private void PreserveTime_Changed(object sender, RoutedEventArgs e) => UpdateTimeState();
    private void UpdateTimeState()
    {
        if (TimeBox is not null) TimeBox.IsEnabled = PreserveTimeBox?.IsChecked != true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!DateBox.SelectedDate.HasValue) { ErrorText.Text = "Выберите дату."; return; }
        var time = TimeSpan.Zero;
        if (!PreserveExistingTime)
        {
            var formats = new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };
            if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), formats, CultureInfo.InvariantCulture, out time) || time.TotalDays >= 1)
            { ErrorText.Text = "Введите время ЧЧ:ММ или ЧЧ:ММ:СС."; return; }
        }
        CaptureDate = DateBox.SelectedDate.Value.Date + time;
        DialogResult = true;
    }
}
