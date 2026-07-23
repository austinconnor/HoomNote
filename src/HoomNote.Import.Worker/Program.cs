using System.Diagnostics;

return await SlideImportWorker.RunAsync(args);

internal static class SlideImportWorker
{
    private static readonly string[] AllowedExtensions = [".ppt", ".pptx"];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || !string.Equals(args[0], "convert", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: HoomNote.Import.Worker convert <presentation> <output-directory>");
            return 2;
        }

        var source = Path.GetFullPath(args[1]);
        var outputDirectory = Path.GetFullPath(args[2]);
        if (!File.Exists(source) || !AllowedExtensions.Contains(Path.GetExtension(source).ToLowerInvariant()))
        {
            Console.Error.WriteLine("Only existing PPT and PPTX files can be converted.");
            return 3;
        }

        Directory.CreateDirectory(outputDirectory);
        var profileDirectory = Path.Combine(outputDirectory, ".lo-profile");
        Directory.CreateDirectory(profileDirectory);
        var soffice = FindLibreOffice();
        if (soffice is null)
        {
            Console.Error.WriteLine("LibreOffice is not installed. Install the HoomNote Slide Import Pack and try again.");
            return 4;
        }

        var profileUri = new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var startInfo = new ProcessStartInfo
        {
            FileName = soffice,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--safe-mode");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--nodefault");
        startInfo.ArgumentList.Add("--nolockcheck");
        startInfo.ArgumentList.Add($"-env:UserInstallation={profileUri}");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add("pdf:impress_pdf_Export");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(source);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("LibreOffice could not be started.");
            return 5;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Console.Error.WriteLine("Slide conversion timed out after two minutes.");
            return 6;
        }

        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
            return process.ExitCode;
        }

        Console.WriteLine(standardOutput.Trim());
        return 0;
    }

    private static string? FindLibreOffice()
    {
        var configured = Environment.GetEnvironmentVariable("HOOMNOTE_LIBREOFFICE_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "SlideImportPack", "program", "soffice.com"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.com"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.com")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}

