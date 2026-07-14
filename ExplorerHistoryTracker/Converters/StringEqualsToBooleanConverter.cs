using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ExplorerHistoryTracker.Converters
{
    /// <summary>
    /// Returns true if the bound string property matches the converter parameter. Supports two-way binding.
    /// </summary>
    public class StringEqualsToBooleanConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return false;
            return value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
            {
                return parameter.ToString()!;
            }
            return AvaloniaProperty.UnsetValue;
        }
    }
}
