using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EtherCAT.NET.Mvvm;

/// <summary>
/// Minimal hand-rolled <see cref="INotifyPropertyChanged"/> base class — the entire MVVM
/// dependency for this milestone's UI (no MVVM NuGet package). A property setter calls
/// <see cref="SetField{T}"/>, which only raises <see cref="PropertyChanged"/> when the value
/// actually changed.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises
    /// <see cref="PropertyChanged"/> for <paramref name="propertyName"/> (defaulting to the calling
    /// property's own name) — but only when the new value is not equal to the old one.
    /// </summary>
    /// <returns><c>true</c> if the value actually changed (and the event was raised); <c>false</c> if it was left unchanged.</returns>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="propertyName"/> (defaulting to the caller's own name).</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
