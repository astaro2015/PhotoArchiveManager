using System.Windows;
using PhotoArchiveManager.Services;
using PhotoArchiveManager.ViewModels;

namespace PhotoArchiveManager;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(x => string.Equals(x, "--portable-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            var exitCode = PortableSelfTest.Run();
            Shutdown(exitCode);
            return;
        }

        try
        {
            AppPaths.EnsureCreated();
            LoggingService.Initialize(AppPaths.LogsDirectory);
            LoggingService.Info($"Photo Archive Manager {AppPaths.AppVersion} starting. Data: {AppPaths.DataDirectory}");

            var database = new DatabaseService(AppPaths.DatabasePath);
            await database.InitializeAsync();

            var metadata = new MetadataService();
            var thumbnails = new ThumbnailService(AppPaths.ThumbnailDirectory);
            var scanner = new LibraryScanner(database, metadata, thumbnails);
            var duplicateAnalyzer = new ExactDuplicateAnalyzer(database);
            var perceptualAnalyzer = new PerceptualHashAnalyzer(database);
            var faceModels = new FaceModelService(AppPaths.ModelDirectory);
            var faceQualityAnalyzer = new FaceQualityAnalyzer(faceModels);
            var qualityAnalyzer = new QualityAnalyzer(database, faceQualityAnalyzer);
            var burstAnalyzer = new BurstAnalyzer(database);
            var peopleAnalyzer = new PeopleAnalyzer(database, faceModels, AppPaths.FaceThumbnailDirectory);
            var eventAnalyzer = new EventAnalyzer(database, metadata);
            var organization = new OrganizationService(database);
            var quarantine = new QuarantineService(database, AppPaths.QuarantineDirectory);
            var viewModel = new MainViewModel(database, scanner, duplicateAnalyzer, perceptualAnalyzer, qualityAnalyzer, burstAnalyzer, peopleAnalyzer, eventAnalyzer, organization, quarantine);
            await viewModel.InitializeAsync();

            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Fatal startup error", ex);
            MessageBox.Show(
                "Не удалось запустить Photo Archive Manager.\n\n" + ex.Message +
                "\n\nПодробности записаны в журнал программы.",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LoggingService.Info("Application exiting.");
        base.OnExit(e);
    }
}
