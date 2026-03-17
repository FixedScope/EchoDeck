using EchoDeck.Core.Models;
using EchoDeck.Core.Services;

namespace EchoDeck.Core.Pipeline;

public class AudioGenerator(TtsRouter ttsRouter, FFmpegService ffmpegService)
{
    private const double DefaultSilenceDurationSeconds = 3.0;

    public async Task GenerateAsync(
        List<Slide> slides,
        string voiceId,
        string outputDir,
        int maxConcurrent,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        var semaphore = new SemaphoreSlim(maxConcurrent);
        int completed = 0;

        var tasks = slides.Select(async slide =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                if (slide.IsSilent)
                {
                    var silencePath = Path.Combine(outputDir, $"slide_{slide.Index:D3}.mp3");
                    await ffmpegService.CreateSilenceAsync(silencePath, DefaultSilenceDurationSeconds, ct);
                    slide.AudioPath = silencePath;
                    slide.DurationSeconds = DefaultSilenceDurationSeconds;
                }
                else
                {
                    var audioBytes = await ttsRouter.TextToSpeechAsync(voiceId, slide.SpeakerNotes, ct);
                    var audioPath = Path.Combine(outputDir, $"slide_{slide.Index:D3}.mp3");
                    await File.WriteAllBytesAsync(audioPath, audioBytes, ct);
                    slide.AudioPath = audioPath;
                    slide.DurationSeconds = await ffmpegService.GetDurationAsync(audioPath, ct);
                }

                var count = Interlocked.Increment(ref completed);
                progress?.Report($"{count}/{slides.Count} audio segments generated");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}
