using System.Windows;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager;

public partial class DuplicateWizardWindow : Window
{
    public DuplicateCleanupRecommendation? SelectedRecommendation => RecommendationsGrid.SelectedItem as DuplicateCleanupRecommendation;

    public DuplicateWizardWindow(IReadOnlyList<DuplicateCleanupRecommendation> recommendations)
    {
        InitializeComponent();
        RecommendationsGrid.ItemsSource = recommendations;
        RecommendationsGrid.SelectedIndex = recommendations.Count > 0 ? 0 : -1;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecommendation is null)
        {
            MessageBox.Show("Выберите группу дублей.", "Мастер дублей", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }
}
