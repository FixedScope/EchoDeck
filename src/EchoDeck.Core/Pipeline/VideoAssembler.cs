using EchoDeck.Core.Models;
using EchoDeck.Core.Services;

namespace EchoDeck.Core.Pipeline;

public class VideoAssembler(FFmpegService ffmpegService)
{
    private const double DefaultSilentSlideDuration = 3.0;

    public async Task<string> AssembleAsync(
        List<Slide> slides,
        string outputDir,
        int width,
        int height,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var segmentsDir = Path.Combine(outputDir, "segments");
        Directory.CreateDirectory(segmentsDir);

        var segmentPaths = new List<string>();

        for (int i = 0; i < slides.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var slide = slides[i];
            var segmentPath = Path.Combine(segmentsDir, $"segment_{i:D3}.mp4");

            if (slide.ImagePath == null)
                throw new InvalidOperationException($"Slide {i} has no image path.");

            if (slide.AudioPath != null && !slide.IsSilent)
            {
                await ffmpegService.CreateSlideSegmentAsync(
                    slide.ImagePath, slide.AudioPath, segmentPath, width, height, ct);
            }
            else
            {
                await ffmpegService.CreateSilentSlideSegmentAsync(
                    slide.ImagePath, segmentPath, DefaultSilentSlideDuration, width, height, ct);
            }

            segmentPaths.Add(segmentPath);
            progress?.Report($"{i + 1}/{slides.Count} segments encoded");
        }

        var rawOutput = Path.Combine(outputDir, "output_raw.mp4");
        var finalOutput = Path.Combine(outputDir, "output.mp4");

        await ffmpegService.ConcatenateSegmentsAsync(segmentPaths, rawOutput, ct);
        progress?.Report("Concatenated segments");

        await ffmpegService.NormalizeAudioAsync(rawOutput, finalOutput, ct);
        progress?.Report("Audio normalized");

        File.Delete(rawOutput);
        return finalOutput;
    }
}
