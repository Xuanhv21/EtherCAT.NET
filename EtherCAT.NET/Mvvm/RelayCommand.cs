using System.Windows.Input;

namespace EtherCAT.NET.Mvvm;

/// <summary>
/// Minimal hand-rolled <see cref="ICommand"/> — the other half of this milestone's tiny MVVM base
/// (no MVVM NuGet package). Wraps a parameterless <see cref="Action"/> plus an optional
/// <see cref="Func{TResult}"/> predicate for <see cref="CanExecute"/>.
/// </summary>
/// <remarks>
/// <see cref="CanExecuteChanged"/> is wired to WPF's <see cref="CommandManager.RequerySuggested"/> so
/// bound buttons re-query automatically on the usual UI events (focus change, mouse/keyboard
/// activity) — the standard lightweight pattern for a hand-rolled <see cref="RelayCommand"/>.
/// <see cref="RaiseCanExecuteChanged"/> is also provided so a view model can force an immediate
/// requery right after changing whatever state <see cref="CanExecute"/> depends on (e.g. after
/// Start/Stop flips <c>IsRunning</c>), without waiting for the next UI event.
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates a command that invokes <paramref name="execute"/>, optionally gated by <paramref name="canExecute"/> (always executable if omitted).</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute();

    /// <summary>Forces WPF to immediately re-query <see cref="CanExecute"/> for every command using this pattern.</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
