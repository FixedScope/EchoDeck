using EchoDeck.Core.Models;
using EchoDeck.Core.Pipeline;
using EchoDeck.Core.Services;

namespace EchoDeck.Core.Tests;

/// <summary>
/// End-to-end pipeline test: MockRenderer + real ElevenLabs + real FFmpeg → output.mp4
/// Requires ELEVENLABS_API_KEY and ELEVENLABS_VOICES env vars (or .env loaded externally).
/// Skipped automatically if API key is absent.
/// </summary>
public class PipelineIntegrationTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), "echodeck-test-" + Guid.NewGuid().ToString("N")[..8]);

    public PipelineIntegrationTests() => Directory.CreateDirectory(_outputDir);

    [Fact]
    public async Task FullPipeline_WithMockRenderer_ProducesOutputMp4()
    {
        var apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        var voices = Environment.GetEnvironmentVariable("ELEVENLABS_VOICES");

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("your_"))
        {
            // Skip — no API key configured
            return;
        }

        var assemblyDir = Path.GetDirectoryName(typeof(PipelineIntegrationTests).Assembly.Location)!;
        var fixturePath = Path.Combine(assemblyDir, "fixtures", "sample.pptx");
        if (!File.Exists(fixturePath))
        {
            return; // Skip — no fixture
        }

        var options = new EchoDeckOptions
        {
            ElevenLabsApiKey = apiKey,
            ElevenLabsVoices = voices ?? string.Empty,
            DataDir = _outputDir,
            SlideRenderer = "mock",
            DefaultResolution = "1280x720", // smaller for faster test
            MaxConcurrentTts = 2,
        };

        var voiceList = options.ParseVoices();
        Assert.NotEmpty(voiceList);
        var voiceId = voiceList[0].Id;

        // Wire up services
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.elevenlabs.io/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        httpClient.DefaultRequestHeaders.Add("xi-api-key", apiKey);

        var elevenLabsClient = new ElevenLabsClient(httpClient);
        var ffmpegService = new FFmpegService();
        var slideExtractor = new SlideExtractor();
        var slideRenderer = new MockSlideRenderer();
        var audioGenerator = new AudioGenerator(elevenLabsClient, ffmpegService);
        var videoAssembler = new VideoAssembler(ffmpegService);
        var orchestrator = new PipelineOrchestrator(slideExtractor, slideRenderer, audioGenerator, videoAssembler, options);

        var job = new VideoJob();

        var progressLog = new List<string>();
        var progress = new Progress<ProgressEvent>(e =>
            progressLog.Add($"[{e.StepName}] {e.StepStatus}: {e.Detail}"));

        using var stream = File.OpenRead(fixturePath);
        var pptxPath = Path.Combine(_outputDir, "input.pptx");
        File.Copy(fixturePath, pptxPath);

        await orchestrator.RunAsync(job, stream, pptxPath, voiceId, progress);

        // Print progress log for visibility
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
