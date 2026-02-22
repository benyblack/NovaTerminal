using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NovaTerminal.Core;

namespace NovaTerminal.Core
{
    public class DirectionConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferDirection dir)
            {
                return dir == TransferDirection.Upload ? "⬆" : "⬇";
            }
            return "?";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class RunningConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferState state) return state == TransferState.Running;
            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class StateBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferState state)
            {
                return state switch
                {
                    TransferState.Running => Brushes.SkyBlue,
                    TransferState.Completed => Brushes.LimeGreen,
                    TransferState.Failed => Brushes.Red,
                    TransferState.Canceled => Brushes.Gray,
                    _ => Brushes.White
                };
            }
            return Brushes.White;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
