using System;
using System.Globalization;
using System.Windows.Data;

namespace GitHub.BhaaLseN.VSIX.Converters
{
    public class SourceControlWindowTitleConverter : IValueConverter
    {
        private readonly VSXPackage _package;
        private readonly IValueConverter _innerConverter;

        public SourceControlWindowTitleConverter(VSXPackage package, IValueConverter innerConverter)
        {
            _package = package;
            _innerConverter = innerConverter;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            object newValue = value;
            if (_innerConverter != null)
                newValue = _innerConverter.Convert(value, targetType, parameter, culture);

            // prepend the branch name, if any.
            if (targetType == typeof(string) && !string.IsNullOrEmpty(_package.BranchName))
                newValue = string.Format("{0} - {1}", _package.BranchName, newValue);

            return newValue;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (_innerConverter != null)
                return _innerConverter.ConvertBack(value, targetType, parameter, culture);
            throw new NotSupportedException();
        }
    }
}
