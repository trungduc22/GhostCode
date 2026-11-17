using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GhostCode.App.Converters;

public sealed class DiffKindToBrushConverter : IValueConverter
{
    public bool IsForeground { get; set; }
    public bool IsSidePanel { get; set; }
    public bool IsLeftSide { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DiffLineKind kind)
            return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        bool isDark = Application.Current?.RequestedTheme == ApplicationTheme.Dark;

        if (IsForeground)
        {
            return kind switch
            {
                DiffLineKind.Added => new SolidColorBrush(isDark ? Color.FromArgb(255, 86, 211, 100) : Color.FromArgb(255, 26, 127, 55)),
                DiffLineKind.Removed => new SolidColorBrush(isDark ? Color.FromArgb(255, 255, 123, 114) : Color.FromArgb(255, 207, 34, 46)),
                DiffLineKind.Modified => new SolidColorBrush(isDark ? Color.FromArgb(255, 227, 179, 65) : Color.FromArgb(255, 154, 103, 0)),
                _ => new SolidColorBrush(isDark ? Color.FromArgb(140, 230, 237, 243) : Color.FromArgb(140, 31, 35, 40))
            };
        }

        if (IsSidePanel)
        {
            if (kind == DiffLineKind.Modified)
                return new SolidColorBrush(IsLeftSide
                    ? (isDark ? Color.FromArgb(44, 248, 81, 73) : Color.FromArgb(30, 255, 129, 130))
                    : (isDark ? Color.FromArgb(44, 46, 160, 67) : Color.FromArgb(30, 45, 164, 78)));
            if (kind == DiffLineKind.Removed && IsLeftSide)
                return new SolidColorBrush(isDark ? Color.FromArgb(52, 248, 81, 73) : Color.FromArgb(34, 255, 129, 130));
            if (kind == DiffLineKind.Added && !IsLeftSide)
                return new SolidColorBrush(isDark ? Color.FromArgb(52, 46, 160, 67) : Color.FromArgb(34, 45, 164, 78));
            if ((kind == DiffLineKind.Removed && !IsLeftSide) || (kind == DiffLineKind.Added && IsLeftSide))
                return new SolidColorBrush(isDark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0));
            return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        return kind switch
        {
            DiffLineKind.Added => new SolidColorBrush(isDark ? Color.FromArgb(48, 46, 160, 67) : Color.FromArgb(34, 45, 164, 78)),
            DiffLineKind.Removed => new SolidColorBrush(isDark ? Color.FromArgb(48, 248, 81, 73) : Color.FromArgb(34, 255, 129, 130)),
            DiffLineKind.Modified => new SolidColorBrush(isDark ? Color.FromArgb(40, 210, 153, 34) : Color.FromArgb(32, 212, 167, 44)),
            _ => new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StorageStatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isDark = Application.Current?.RequestedTheme == ApplicationTheme.Dark;
        if (value is not StorageStatusKind kind)
            return new SolidColorBrush(isDark ? Color.FromArgb(255, 140, 149, 159) : Color.FromArgb(255, 110, 119, 129));

        return kind switch
        {
            StorageStatusKind.Synced => new SolidColorBrush(isDark ? Color.FromArgb(255, 63, 185, 80) : Color.FromArgb(255, 26, 127, 55)),
            StorageStatusKind.Pending => new SolidColorBrush(isDark ? Color.FromArgb(255, 210, 153, 34) : Color.FromArgb(255, 154, 103, 0)),
            StorageStatusKind.CloudOnly => new SolidColorBrush(isDark ? Color.FromArgb(255, 88, 166, 255) : Color.FromArgb(255, 9, 105, 218)),
            StorageStatusKind.SyncError => new SolidColorBrush(isDark ? Color.FromArgb(255, 248, 81, 73) : Color.FromArgb(255, 207, 34, 46)),
            StorageStatusKind.Conflict => new SolidColorBrush(isDark ? Color.FromArgb(255, 219, 109, 40) : Color.FromArgb(255, 188, 76, 0)),
            _ => new SolidColorBrush(isDark ? Color.FromArgb(255, 139, 148, 158) : Color.FromArgb(255, 101, 109, 118))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
