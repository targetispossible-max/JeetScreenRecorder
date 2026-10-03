using System.Windows.Input;

namespace JeetScreenRecorder.Utils;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
