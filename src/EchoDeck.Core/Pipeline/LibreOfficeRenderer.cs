using System.Runtime.InteropServices;

namespace EchoDeck.Core.Pipeline;

public class LibreOfficeRenderer(string libreOfficePath) : ISlideRenderer
{
    private readonly string _libreOfficePath = ResolveLibreOfficePath(libreOfficePath);

    public async Task<List<string>> RenderAsync(string pptxPath, int slideCount, string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        // LibreOffice exports all slides as PNG to outputDir
        await RunLibreOfficeAsync(
            $"--headless --convert-to png --outdir \"{outputDir}\" \"{pptxPath}\"",
            ct);

        // LibreOffice names files as <basename>1.png, <basename>2.png, etc.
        var baseName = Path.GetFileNameWithoutExtension(pptxPath);
        var paths = new List<string>();

        for (int i = 0; i < slideCount; i++)
        {
            // LibreOffice uses 1-based numbering
            var libreOfficeFile = Path.Combine(outputDir, $"{baseName}{i + 1}.png");
            var normalizedPath = Path.Combine(outputDir, $"slide_{i:D3}.png");

            if (File.Exists(libreOfficeFile))
            {
                File.Move(libreOfficeFile, normalizedPath, overwrite: true);
                paths.Add(normalizedPath);
            }
            else
            {
                throw new FileNotFoundException(
                    $"LibreOffice did not produce slide {i + 1}. Expected: {libreOfficeFile}");
            }
        }

        return paths;
    }

    public async Task<string?> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            await RunLibreOfficeAsync("--version", ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"LibreOffice health check failed: {ex.Message}";
        }
    }

    private async Task RunLibreOfficeAsync(string args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _libreOfficePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start LibreOffice process.");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"LibreOffice exited with code {process.ExitCode}: {stderr}");
        }
    }

    private static string ResolveLibreOfficePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? @"C:\Program Files\LibreOffice\program\soffice.exe"
            : "libreoffice";
    }
}
