using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ExplorerHistoryTracker.Converters
{
    /// <summary>
    /// Inverts a boolean value (true -> false, false -> true). Supports two-way binding.
    /// </summary>
    public class InverseBooleanConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b)
                return !b;
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b)
                return !b;
            return false;
        }
    }
}
