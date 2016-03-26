using EnvDTE;

namespace GitHub.BhaaLseN.VSIX
{
    internal static class EnvDTEExtensionMethods
    {
        public static T GetPropertyValue<T>(this Properties properties, string propertyName)
        {
            try
            {
                return (T)properties?.Item(propertyName)?.Value;
            }
            catch
            {
                return default(T);
            }
        }
    }
}
