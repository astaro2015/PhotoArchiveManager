using PhotoArchiveManager.Services;
using System.Windows;

namespace PhotoArchiveManager;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Title = $"Photo Archive Manager · {AppPaths.AppVersion} · Справка";
    }
}
