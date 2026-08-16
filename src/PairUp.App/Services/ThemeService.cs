using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace PairUp.App.Services;

/// <summary>
/// Detects Windows' light/dark app theme (registry-based, same setting Settings > Personalization
/// > Colors reads) and swaps PairUp's merged theme dictionary to match — including live, if the
/// user changes it while the app is running.
/// </summary>
public static class ThemeService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RegistryValue = "AppsUseLightTheme";

    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var value = key?.GetValue(RegistryValue);
            return value is int i && i == 1;
        }
        catch
        {
            return false; // default to dark if the registry read fails for any reason
        }
    }

    public static void Apply(bool light)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var themeUri = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);

        var existing = dictionaries.FirstOrDefault(d =>
            d.Source != null && d.Source.OriginalString.Contains("Themes/"));

        var newDictionary = new ResourceDictionary { Source = themeUri };

        if (existing != null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = newDictionary;
        }
        else
        {
            dictionaries.Insert(0, newDictionary);
        }
    }

    public static void ApplyCurrentSystemTheme() => Apply(IsLightTheme());
}
