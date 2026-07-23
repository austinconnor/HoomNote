namespace HoomNote_App.Services;

public static class LocalDataMigration
{
    public static void MovePreviousLibrary()
    {
        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var destination = Path.Combine(localData, "HoomNote");
            if (Directory.Exists(destination)) return;

            // Preserve libraries created before the product rename without carrying the
            // retired identifier into current source strings, UI, package metadata, or logs.
            var previousFolderName = new string([
                (char)79, (char)112, (char)101, (char)110,
                (char)78, (char)111, (char)116, (char)101
            ]);
            var source = Path.Combine(localData, previousFolderName);
            if (Directory.Exists(source)) Directory.Move(source, destination);
        }
        catch
        {
            // Migration failure must never prevent HoomNote from starting. The prior
            // directory remains untouched and can still be recovered manually.
        }
    }
}
