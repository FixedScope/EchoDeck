using System.Diagnostics;
using System.Text;

namespace EchoDeck.Core.Services;

public class FFmpegService
{
    public async Task<string?> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            await RunAsync("-version", ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"FFmpeg health check failed: {ex.Message}";
        }
    }

    public async Task<double> GetDurationAsync(string filePath, CancellationToken ct = default)
    {
        // ffprobe is more reliable for duration, but ffmpeg can do it too
        var args = $"-i \"{filePath}\" -f null -";
        try
        {
            // ffmpeg prints duration to stderr
            var (_, stderr) = await RunWithOutputAsync(args, ct);
            var match = System.Text.RegularExpressions.Regex.Match(
                stderr, @"Duration:\s*(\d+):(\d+):(\d+\.?\d*)");
            if (match.Success)
            {
                var h = double.Parse(match.Groups[1].Value);
                var m = double.Parse(match.Groups[2].Value);
                var s = double.Parse(match.Groups[3].Value);
                return h * 3600 + m * 60 + s;
            }
        }
        catch { /* fall through */ }
        return 0;
    }

    public async Task CreateSilenceAsync(string outputPath, double durationSeconds, CancellationToken ct = default)
    {
        var args = $"-f lavfi -i anullsrc=r=44100:cl=stereo -t {durationSeconds:F3} -c:a aac -y \"{outputPath}\"";
        await RunAsync(args, ct);
    }

    public async Task CreateSlideSegmentAsync(
        string imagePath,
        string audioPath,
        string outputPath,
        int width,
        int height,
        CancellationToken ct = default)
    {
        var scaleFilter = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";
        var args = $"-loop 1 -i \"{imagePath}\" -i \"{audioPath}\" " +
                   $"-c:v libx264 -tune stillimage -c:a aac " +
                   $"-pix_fmt yuv420p -shortest " +
                   $"-vf \"{scaleFilter}\" " +
                   $"-r 30 -y \"{outputPath}\"";
        await RunAsync(args, ct);
    }

    public async Task CreateSilentSlideSegmentAsync(
        string imagePath,
        string outputPath,
        double durationSeconds,
        int width,
        int height,
        CancellationToken ct = default)
    {
        var scaleFilter = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";
        var args = $"-loop 1 -i \"{imagePath}\" -t {durationSeconds:F3} " +
                   $"-c:v libx264 -tune stillimage " +
                   $"-pix_fmt yuv420p " +
                   $"-vf \"{scaleFilter}\" " +
                   $"-r 30 -y \"{outputPath}\"";
        await RunAsync(args, ct);
    }

    public async Task ConcatenateSegmentsAsync(
        List<string> segmentPaths,
        string outputPath,
        CancellationToken ct = default)
    {
        var concatFile = Path.Combine(Path.GetDirectoryName(outputPath)!, "concat.txt");
        var lines = segmentPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'");
        await File.WriteAllLinesAsync(concatFile, lines, ct);

        var args = $"-f concat -safe 0 -i \"{concatFile}\" -c copy -y \"{outputPath}\"";
        await RunAsync(args, ct);

        File.Delete(concatFile);
    }

    public async Task<byte[]> ConvertPcmToMp3Async(byte[] pcmBytes, CancellationToken ct = default)
    {
        var tempPcm = Path.GetTempFileName() + ".pcm";
        var tempMp3 = Path.GetTempFileName() + ".mp3";
        try
        {
            await File.WriteAllBytesAsync(tempPcm, pcmBytes, ct);
            var args = $"-f s16le -ar 24000 -ac 1 -i \"{tempPcm}\" -y \"{tempMp3}\"";
            await RunAsync(args, ct);
            return await File.ReadAllBytesAsync(tempMp3, ct);
        }
        finally
        {
            if (File.Exists(tempPcm)) File.Delete(tempPcm);
            if (File.Exists(tempMp3)) File.Delete(tempMp3);
        }
    }

    public async Task NormalizeAudioAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        var args = $"-i \"{inputPath}\" -af loudnorm=I=-16:TP=-1.5:LRA=11 -c:v copy -y \"{outputPath}\"";
        await RunAsync(args, ct);
    }

    private static async Task<string> RunAsync(string args, CancellationToken ct)
    {
        var (stdout, stderr) = await RunWithOutputAsync(args, ct);
        return stdout + stderr;
    }

    private static async Task<(string Stdout, string Stderr)> RunWithOutputAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg process.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}.\nArgs: {args}\nStderr: {stderr}");

        return (stdout.ToString(), stderr.ToString());
    }
}
