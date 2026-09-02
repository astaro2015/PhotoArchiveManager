using System.Windows;
using System.Windows.Input;
using PhotoArchiveManager.Services;

namespace PhotoArchiveManager.Infrastructure;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome for user-stoppable operations.
        }
        catch (Exception ex)
        {
            // ICommand.Execute is necessarily void. Without an explicit catch, an exception
            // escaping an awaited command is rethrown on the WPF dispatcher and may terminate
            // the whole application. Keep the failure local, log it, and leave PAM usable.
            LoggingService.Error("Unhandled async command error", ex);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                MessageBox.Show(
                    "Операция не выполнена.\n\n" + ex.Message + "\n\nПодробности записаны в журнал программы.",
                    "Ошибка операции",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                dispatcher.Invoke(() => MessageBox.Show(
                    "Операция не выполнена.\n\n" + ex.Message + "\n\nПодробности записаны в журнал программы.",
                    "Ошибка операции",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));
            }
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
