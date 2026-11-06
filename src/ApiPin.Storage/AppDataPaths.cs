namespace ApiPin.Storage;

/// <summary>
/// Where app-level data lives (settings, open tabs, secrets, history, logs): <c>%APPDATA%\ApiPin</c>, or the folder in
/// the <c>APIPIN_DATA_DIR</c> environment variable (portable use, or trying a build without touching your own data).
/// </summary>
public static class AppDataPaths
{
    public const string OverrideVariable = "APIPIN_DATA_DIR";

    public static string Root =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApiPin");

    public static string File(string name) => Path.Combine(Root, name);
}
