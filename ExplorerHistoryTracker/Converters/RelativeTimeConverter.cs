using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ExplorerHistoryTracker.Converters
{
    /// <summary>
    /// Converts a DateTime object into a relative friendly time string (e.g. "刚刚", "5分钟前", "昨天 14:32").
    /// </summary>
    public class RelativeTimeConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                TimeSpan span = DateTime.Now - dateTime;

                // Handle future date edge cases due to clock skew
                if (span.TotalSeconds < 0)
                {
                    return "刚刚";
                }

                if (span.TotalSeconds < 60)
                {
                    return "刚刚";
                }
                if (span.TotalMinutes < 60)
                {
                    return $"{(int)span.TotalMinutes}分钟前";
                }
                if (span.TotalHours < 24)
                {
                    return $"{(int)span.TotalHours}小时前";
                }
                if (span.TotalDays < 2)
                {
                    return $"昨天 {dateTime:HH:mm}";
                }
                if (span.TotalDays < 7)
                {
                    return $"{(int)span.TotalDays}天前";
                }

                return dateTime.ToString("yyyy-MM-dd HH:mm");
            }
            return string.Empty;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
