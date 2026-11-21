using System.Globalization;

namespace FragileDiskCopy;

internal enum UiLanguage
{
    Chinese,
    English
}

internal static class UiText
{
    public static UiLanguage Current { get; set; } =
        GetDefaultLanguage(CultureInfo.CurrentUICulture);
    public static bool IsEnglish => Current == UiLanguage.English;

    public static string Get(string chinese, string english)
    {
        return IsEnglish ? english : chinese;
    }

    public static UiLanguage GetDefaultLanguage(CultureInfo culture)
    {
        return culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Chinese
            : UiLanguage.English;
    }
}
