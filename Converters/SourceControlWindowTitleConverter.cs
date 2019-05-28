using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace GitHub.BhaaLseN.VSIX.Converters
{
    public class SourceControlWindowTitleConverter : IValueConverter
    {
        private readonly VSXPackage _package;
        private readonly IValueConverter _innerConverter;
        private readonly TitleConverterPlacement _placement;
        private readonly TitleConverterSeparator _separator;

        public SourceControlWindowTitleConverter(VSXPackage package, IValueConverter innerConverter, TitleConverterPlacement placement = TitleConverterPlacement.Front, TitleConverterSeparator separator = TitleConverterSeparator.Dash)
        {
            _package = package;
            _innerConverter = innerConverter;
            _placement = placement;
            _separator = separator;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            object newValue = value;
            if (_innerConverter != null)
                newValue = _innerConverter.Convert(value, targetType, parameter, culture);

            // prepend the branch name, if any.
            if (targetType == typeof(string) && !string.IsNullOrEmpty(_package.BranchName))
                newValue = BuildNewValue(newValue);

            return newValue;
        }

        private static readonly Dictionary<TitleConverterSeparator, char> SeparatorMap = new Dictionary<TitleConverterSeparator, char>
        {
            { TitleConverterSeparator.Dash, '-' },
            { TitleConverterSeparator.Pipe, '|' },
        };
        private string BuildNewValue(object newValue)
        {
            if (!SeparatorMap.TryGetValue(_separator, out char separator))
                separator = '-';

            object front = _placement == TitleConverterPlacement.Front ? _package.BranchName : newValue;
            object back = _placement == TitleConverterPlacement.Front ? newValue : _package.BranchName;

            return string.Format("{0} {2} {1}", front, back, separator);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (_innerConverter != null)
                return _innerConverter.ConvertBack(value, targetType, parameter, culture);
            throw new NotSupportedException();
        }
    }

    public enum TitleConverterPlacement
    {
        Front = 0,
        Back = 1,
    }
    public enum TitleConverterSeparator
    {
        Dash = 0,
        Pipe = 1,
    }
}
