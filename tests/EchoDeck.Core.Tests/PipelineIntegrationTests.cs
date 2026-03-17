using EchoDeck.Core.Models;
using EchoDeck.Core.Pipeline;
using EchoDeck.Core.Services;

namespace EchoDeck.Core.Tests;

/// <summary>
/// End-to-end pipeline tests using real TTS + MockRenderer + real FFmpeg.
/// Each test is skipped automatically if the required API key is absent.
/// </summary>
public class PipelineIntegrationTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), "echodeck-test-" + Guid.NewGuid().ToString("N")[..8]);

    public PipelineIntegrationTests() => Directory.CreateDirectory(_outputDir);

    [Fact]
    public async Task FullPipeline_WithElevenLabs_ProducesOutputMp4()
    {
        var apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        var voices = Environment.GetEnvironmentVariable("ELEVENLABS_VOICES");

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("your_"))
            return;

        var fixturePath = GetFixturePath();
        if (fixturePath == null) return;

        var options = new EchoDeckOptions
        {
            ElevenLabsApiKey = apiKey,
            ElevenLabsVoices = voices ?? string.Empty,
            DataDir = _outputDir,
            SlideRenderer = "mock",
            DefaultResolution = "1280x720",
            MaxConcurrentTts = 2,
        };

        var voiceId = options.ParseVoices()[0].Id;
        var (orchestrator, job) = BuildPipeline(options, elevenLabsKey: apiKey, geminiKey: null);

        await RunAndAssert(orchestrator, job, fixturePath);
    }

    [Fact]
    public async Task FullPipeline_WithGemini_ProducesOutputMp4()
    {
        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrWhiteSpace(geminiKey) || geminiKey.StartsWith("your_"))
            return;

        var fixturePath = GetFixturePath();
        if (fixturePath == null) return;

        var options = new EchoDeckOptions
        {
            GeminiApiKey = geminiKey,
            DataDir = _outputDir,
            SlideRenderer = "mock",
            DefaultResolution = "1280x720",
            MaxConcurrentTts = 1, // free tier is ~2 RPM, serialize to avoid 429s
        };

        var voiceId = "gemini:Kore";
        var (orchestrator, job) = BuildPipeline(options, elevenLabsKey: null, geminiKey: geminiKey);

        await RunAndAssert(orchestrator, job, fixturePath, voiceId);
    }

    private static string? GetFixturePath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(PipelineIntegrationTests).Assembly.Location)!;
        var path = Path.Combine(assemblyDir, "fixtures", "sample.pptx");
        return File.Exists(path) ? path : null;
    }

    private (PipelineOrchestrator, VideoJob) BuildPipeline(EchoDeckOptions options, string? elevenLabsKey, string? geminiKey)
    {
        var ffmpegService = new FFmpegService();

        ElevenLabsClient? elevenLabsClient = null;
        if (!string.IsNullOrEmpty(elevenLabsKey))
        {
            var http = new HttpClient { BaseAddress = new Uri("https://api.elevenlabs.io/"), Timeout = TimeSpan.FromSeconds(120) };
            http.DefaultRequestHeaders.Add("xi-api-key", elevenLabsKey);
            elevenLabsClient = new ElevenLabsClient(http);
        }

        GeminiTtsClient? geminiClient = null;
        if (!string.IsNullOrEmpty(geminiKey))
        {
            var http = new HttpClient { BaseAddress = new Uri("https://generativelanguage.googleapis.com/"), Timeout = TimeSpan.FromSeconds(120) };
            http.DefaultRequestHeaders.Add("x-goog-api-key", geminiKey);
            geminiClient = new GeminiTtsClient(http, ffmpegService);
        }

        var ttsRouter = new TtsRouter(elevenLabsClient, geminiClient);
        var audioGenerator = new AudioGenerator(ttsRouter, ffmpegService);
        var orchestrator = new PipelineOrchestrator(
            new SlideExtractor(), new MockSlideRenderer(), audioGenerator, new VideoAssembler(ffmpegService), options);

        return (orchestrator, new VideoJob());
    }

    private async Task RunAndAssert(PipelineOrchestrator orchestrator, VideoJob job, string fixturePath, string? voiceId = null)
    {
        var pptxPath = Path.Combine(_outputDir, "input.pptx");
        File.Copy(fixturePath, pptxPath);

        var progressLog = new List<string>();
        var progress = new Progress<ProgressEvent>(e => progressLog.Add($"[{e.StepName}] {e.StepStatus}: {e.Detail}"));

        using var stream = File.OpenRead(fixturePath);

        // default voice: first ElevenLabs voice or first Gemini voice
        voiceId ??= job.GetType().Name; // fallback unused — orchestrator resolves it

        // Resolve default from options if not provided
        await orchestrator.RunAsync(job, stream, pptxPath, voiceId, progress);

        foreach (var line in progressLog)
            Console.WriteLine(line);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.NotNull(job.OutputPath);
        Assert.True(File.Exists(job.OutputPath), $"Expected output.mp4 at: {job.OutputPath}");

        var fileInfo = new FileInfo(job.OutputPath);
        Assert.True(fileInfo.Length > 0, "output.mp4 is empty");

        Console.WriteLine($"\nOutput: {job.OutputPath} ({fileInfo.Length / 1024} KB)");
        Console.WriteLine($"Warnings: {string.Join(", ", job.Warnings)}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { }
    }
}
