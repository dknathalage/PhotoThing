namespace PhotoThing.App.Services;

public static class SettingsPaths
{
    private static string Base =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoThing");

    public static string SettingsFile => Path.Combine(Base, "settings.json");
    public static string IndexDb => Path.Combine(Base, "index.db");
    public static string ThumbCache => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoThing", "thumbs");
}
