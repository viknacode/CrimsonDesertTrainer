using System.Globalization;
using System.Windows.Data;

namespace CrimsonTrainer.Infrastructure;

/// <summary>
/// Binds an enum property to a group of RadioButtons: IsChecked is true when the value
/// equals the ConverterParameter, and checking a button writes that value back.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name && value.ToString() == name;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
