using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DockerDashboard.Models;

namespace DockerDashboard.Converters;

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ContainerStatus status)
        {
            return status switch
            {
                ContainerStatus.Running => new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)),
                ContainerStatus.Restarting => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)),
                ContainerStatus.Exited => new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)),
                ContainerStatus.Dead => new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158)),
                ContainerStatus.Stopped => new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158))
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ContainerStatus status)
        {
            return status switch
            {
                ContainerStatus.Running => "●",
                ContainerStatus.Restarting => "◐",
                ContainerStatus.Exited => "●",
                ContainerStatus.Dead => "✕",
                ContainerStatus.Stopped => "●",
                _ => "○"
            };
        }
        return "○";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        return value != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
