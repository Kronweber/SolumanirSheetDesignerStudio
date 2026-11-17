using System.Text.Json;
using Avalonia.Markup.Xaml;
using SolumanirSheetDesignerStudio.Core.Settings;

namespace SolumanirSheetDesignerStudio.App.Services;

/// <summary>
/// Language-ready strings, gettext style: the English text is the key. A translation is a JSON object
/// { "English text": "Translated text" } in <c>lang/&lt;code&gt;.json</c> next to the app or in the user data folder.
/// The language is chosen once at start-up (strings in XAML are resolved when a view is built).
/// </summary>
public static class Loc
{
    // Replaced as a whole, never modified: worker threads read it while the UI thread may load another language.
    private static volatile IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    public static string Language { get; private set; } = "en";

    public static void Load(string? language)
    {
        Language = string.IsNullOrWhiteSpace(language) ? "en" : language;
        var table = new Dictionary<string, string>();
        if (Language != "en")
        {
            foreach (string dir in new[] { Path.Combine(AppContext.BaseDirectory, "lang"), AppPaths.LanguagesDir })
            {
                string file = Path.Combine(dir, Language + ".json");
                try
                {
                    if (!File.Exists(file)) continue;
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(file));
                    if (loaded == null) continue;
                    foreach (var kv in loaded)
                        if (kv.Value != null) table[kv.Key] = kv.Value;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // a broken language file falls back to English
                }
            }
        }
        _table = table;
    }

    public static string T(string text) => _table.TryGetValue(text, out string? translated) ? translated : text;

    public static string T(string format, params object?[] args)
    {
        try
        {
            return string.Format(T(format), args);
        }
        catch (FormatException)
        {
            return string.Format(format, args); // a translation with a wrong placeholder must not break the command
        }
    }
}

/// <summary>XAML: <c>Text="{l:T 'Add row'}"</c>.</summary>
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
