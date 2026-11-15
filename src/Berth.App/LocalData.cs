namespace Berth;

/// <summary>Where Berth keeps its files: the package's local data folder, or <c>%LOCALAPPDATA%\Berth</c> when run without package identity.</summary>
internal static class LocalData
{
    public static string Directory { get; } = Find();

    private static string Find()
    {
        try
        {
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception)
        {
            // No package identity: ApplicationData.Current throws.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Berth");
        }
    }
}
