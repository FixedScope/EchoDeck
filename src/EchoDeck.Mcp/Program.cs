using EchoDeck.Core.Models;
using EchoDeck.Core.Pipeline;
using EchoDeck.Core.Services;
using EchoDeck.Mcp.Hubs;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Configuration.AddEnvironmentVariables();
var options = new EchoDeckOptions();
builder.Configuration.GetSection(EchoDeckOptions.SectionName).Bind(options);

// Override from environment variables directly (Railway style)
options.ElevenLabsApiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") ?? options.ElevenLabsApiKey;
options.ElevenLabsVoices = Environment.GetEnvironmentVariable("ELEVENLABS_VOICES") ?? options.ElevenLabsVoices;
options.McpAuthKey = Environment.GetEnvironmentVariable("MCP_AUTH_KEY") ?? options.McpAuthKey;
options.TestMode = bool.TryParse(Environment.GetEnvironmentVariable("TEST_MODE"), out var tm) ? tm : options.TestMode;
options.DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? options.DataDir;
options.SlideRenderer = Environment.GetEnvironmentVariable("SLIDE_RENDERER") ?? options.SlideRenderer;
options.JobRetentionHours = int.TryParse(Environment.GetEnvironmentVariable("JOB_RETENTION_HOURS"), out var jrh) ? jrh : options.JobRetentionHours;
options.LibreOfficePath = Environment.GetEnvironmentVariable("LIBREOFFICE_PATH") ?? options.LibreOfficePath;
options.GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? options.GeminiApiKey;

var railwayDomain = Environment.GetEnvironmentVariable("RAILWAY_PUBLIC_DOMAIN");
if (!string.IsNullOrEmpty(railwayDomain))
    options.BaseUrl = $"https://{railwayDomain}";
options.BaseUrl = Environment.GetEnvironmentVariable("BASE_URL") ?? options.BaseUrl;

builder.Services.AddSingleton(options);

// Core services
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<TempFileServer>();
builder.Services.AddSingleton<SlideExtractor>();
builder.Services.AddSingleton<FFmpegService>();
builder.Services.AddSingleton<VideoAssembler>();
builder.Services.AddSingleton<AudioGenerator>();

// Startup validation — at least one TTS provider must be configured
if (string.IsNullOrEmpty(options.ElevenLabsApiKey) && string.IsNullOrEmpty(options.GeminiApiKey))
    throw new InvalidOperationException("At least one TTS provider must be configured. Set ELEVENLABS_API_KEY or GEMINI_API_KEY.");

// ElevenLabs HTTP client (optional)
if (!string.IsNullOrEmpty(options.ElevenLabsApiKey))
{
    builder.Services.AddHttpClient<ElevenLabsClient>(client =>
    {
        client.BaseAddress = new Uri("https://api.elevenlabs.io/");
        client.DefaultRequestHeaders.Add("xi-api-key", options.ElevenLabsApiKey);
        client.Timeout = TimeSpan.FromSeconds(120);
    });
}
else
{
    builder.Services.AddSingleton<ElevenLabsClient>(_ => null!);
}

// Gemini TTS HTTP client (optional)
if (!string.IsNullOrEmpty(options.GeminiApiKey))
{
    builder.Services.AddHttpClient<GeminiTtsClient>(client =>
    {
        client.BaseAddress = new Uri($"https://generativelanguage.googleapis.com/");
        client.DefaultRequestHeaders.Add("x-goog-api-key", options.GeminiApiKey);
        client.Timeout = TimeSpan.FromSeconds(120);
    });
}
else
{
    builder.Services.AddSingleton<GeminiTtsClient>(_ => null!);
}

// TTS router — selects provider based on voice ID prefix
builder.Services.AddSingleton<TtsRouter>();

// Slide renderer — selected by config
builder.Services.AddSingleton<ISlideRenderer>(_ => options.SlideRenderer switch
{
    "libreOffice" => (ISlideRenderer)new LibreOfficeRenderer(options.LibreOfficePath),
    "mock" => new MockSlideRenderer(),
    _ => new OfficeOnlineRenderer(options.BaseUrl),
});

// Pipeline
builder.Services.AddSingleton<PipelineOrchestrator>();

// Background services
builder.Services.AddHostedService<JobCleanupService>();

// MCP server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(EchoDeck.Mcp.Tools.GenerateVideoTool).Assembly);

// SignalR
builder.Services.AddSignalR();

// Static files (for test UI and job output downloads)
builder.Services.AddDirectoryBrowser();

var app = builder.Build();

// Serve job output files
var dataDir = Path.GetFullPath(options.DataDir);
Directory.CreateDirectory(dataDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dataDir),
    RequestPath = "/jobs-data",
    ServeUnknownFileTypes = true,
});

// Temp file endpoint for Office Online
app.MapGet("/temp/{jobId}/{fileName}", (string jobId, string fileName, TempFileServer tempFileServer) =>
{
    var key = $"{jobId}/{fileName}";
    var filePath = tempFileServer.GetFilePath(key);
    if (filePath == null || !File.Exists(filePath))
        return Results.NotFound();
    return Results.File(filePath, "application/vnd.openxmlformats-officedocument.presentationml.presentation");
});

// Test mode REST endpoints (web UI only)
if (options.TestMode)
{

app.MapGet("/api/voices", (EchoDeckOptions opts) =>
{
    var voices = new List<object>();
    if (!string.IsNullOrEmpty(opts.ElevenLabsApiKey))
        voices.AddRange(opts.ParseVoices().Select(v => (object)new { id = v.Id, name = v.Name, provider = "elevenlabs" }));
    if (!string.IsNullOrEmpty(opts.GeminiApiKey))
        voices.AddRange(EchoDeck.Core.Services.GeminiTtsClient.KnownVoices.Select(name => (object)new { id = $"gemini:{name}", name, provider = "gemini" }));
    return Results.Ok(new { voices });
});

app.MapPost("/api/generate", async (
    HttpContext ctx,
    JobStore jobStore,
    PipelineOrchestrator orchestrator,
    TempFileServer tempFileServer,
    EchoDeckOptions opts,
    Microsoft.AspNetCore.SignalR.IHubContext<EchoDeck.Mcp.Hubs.ProgressHub> hubContext,
    ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("GenerateApi");
    var body = await ctx.Request.ReadFromJsonAsync<GenerateRequest>();
    if (body?.FileId == null)
        return Results.BadRequest(new { error = "file_id is required. Upload the .pptx to /upload/pptx first." });

    var uploadDir = Path.Combine(opts.DataDir, "uploads");
    var pptxUploadPath = Path.Combine(uploadDir, body.FileId + ".pptx");
    if (!File.Exists(pptxUploadPath))
        return Results.BadRequest(new { error = $"file_id '{body.FileId}' not found." });

    string? resolvedVoiceId = body.VoiceId;
    if (resolvedVoiceId == null)
    {
        var elVoices = opts.ParseVoices();
        if (elVoices.Count > 0)
            resolvedVoiceId = elVoices[0].Id;
        else if (!string.IsNullOrEmpty(opts.GeminiApiKey))
            resolvedVoiceId = $"gemini:{EchoDeck.Core.Services.GeminiTtsClient.KnownVoices[0]}";
        else
            return Results.BadRequest(new { error = "No TTS voices configured. Set ELEVENLABS_VOICES or GEMINI_API_KEY." });
    }
    if (body.Resolution != null) opts.DefaultResolution = body.Resolution;

    var job = jobStore.CreateJob();
    var jobDir = opts.GetJobDir(job.JobId);
    Directory.CreateDirectory(jobDir);
    var pptxPath = Path.Combine(jobDir, "input.pptx");
    File.Move(pptxUploadPath, pptxPath);
    tempFileServer.Register(job.JobId, pptxPath);

    _ = Task.Run(async () =>
    {
        try
        {
            var progress = new Progress<EchoDeck.Core.Models.ProgressEvent>(async evt =>
            {
                try { await hubContext.Clients.Group(job.JobId).SendAsync("progress", evt); }
                catch { }
            });
            using var stream = File.OpenRead(pptxPath);
            await orchestrator.RunAsync(job, stream, pptxPath, resolvedVoiceId, progress);
            if (job.OutputPath != null)
            {
                var rel = Path.GetRelativePath(opts.DataDir, job.OutputPath).Replace('\\', '/');
                job.OutputUrl = $"{opts.BaseUrl.TrimEnd('/')}/jobs-data/{rel}";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline failed for job {JobId}", job.JobId);
            job.Status = EchoDeck.Core.Models.JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally { tempFileServer.Unregister(job.JobId); }
    });

    return Results.Ok(new { job_id = job.JobId });
});

// REST endpoint for the web UI to poll job status
app.MapGet("/api/job/{jobId}", (string jobId, JobStore jobStore, EchoDeckOptions opts) =>
{
    var job = jobStore.Get(jobId);
    if (job == null) return Results.NotFound(new { error = $"Job '{jobId}' not found." });
    return Results.Ok(new
    {
        job_id = job.JobId,
        status = job.Status.ToString().ToLowerInvariant(),
        progress = job.Progress,
        steps = job.Steps.Select(s => new { name = s.Name, status = s.Status, detail = s.Detail }),
        output_url = job.Status == EchoDeck.Core.Models.JobStatus.Completed ? job.OutputUrl : null,
        warnings = job.Warnings,
        error = job.Error,
    });
});

} // end TestMode endpoints

// PPTX upload endpoint — used by Claude via curl before calling generate_video
app.MapPost("/upload/pptx", async (HttpRequest request, EchoDeckOptions opts) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files["file"];
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No file provided. Use -F \"file=@/path/to/file.pptx\"" });
    if (!file.FileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .pptx files are accepted." });

    var uploadDir = Path.Combine(opts.DataDir, "uploads");
    Directory.CreateDirectory(uploadDir);
    var fileId = Guid.NewGuid().ToString("N");
    var dest = Path.Combine(uploadDir, fileId + ".pptx");
    using var stream = File.Create(dest);
    await file.CopyToAsync(stream);

    return Results.Ok(new { file_id = fileId });
});

// Health check
app.MapGet("/health", async (FFmpegService ffmpeg, ISlideRenderer renderer) =>
{
    var results = new Dictionary<string, string>();

    var ffmpegError = await ffmpeg.HealthCheckAsync();
    results["ffmpeg"] = ffmpegError ?? "ok";

    if (options.SlideRenderer == "officeOnline")
    {
        var rendererError = await renderer.HealthCheckAsync();
        results["slideRenderer"] = rendererError ?? "ok";
    }
    else
    {
        results["slideRenderer"] = $"ok (mode: {options.SlideRenderer})";
    }

    var allOk = results.Values.All(v => v == "ok" || v.StartsWith("ok"));
    return allOk
        ? Results.Ok(new { status = "healthy", checks = results })
        : Results.Json(new { status = "degraded", checks = results }, statusCode: 503);
});

// MCP endpoint
app.MapMcp("/mcp");

// SignalR hub
app.MapHub<ProgressHub>("/hubs/progress");

// Test mode UI
if (options.TestMode)
{
    app.UseDefaultFiles();
    app.UseStaticFiles(); // serves wwwroot/index.html
}

// Startup banner
app.Lifetime.ApplicationStarted.Register(() =>
{
    var baseUrl = options.BaseUrl.TrimEnd('/');
    var providers = new List<string>();
    if (!string.IsNullOrEmpty(options.ElevenLabsApiKey)) providers.Add("ElevenLabs");
    if (!string.IsNullOrEmpty(options.GeminiApiKey)) providers.Add("Gemini");

    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║               EchoDeck is ready!                 ║");
    Console.WriteLine("║ WARNING: THERE IS NO AUTHENTICATION ON THIS MCP! ║");
    Console.WriteLine("╠══════════════════════════════════════════════════╣");
    Console.WriteLine($"║  MCP Server : {baseUrl}/mcp");    
    if (options.TestMode)
        Console.WriteLine($"║  Web UI     : {baseUrl}");
    Console.WriteLine($"║  Voices     : {(providers.Count > 0 ? string.Join(" + ", providers) : "none configured")}");
    Console.WriteLine("╠══════════════════════════════════════════════════╣");
    Console.WriteLine("║  Add to Claude Desktop:                          ║");
    Console.WriteLine("║  Settings → Integrations → Custom Connectors    ║");
    Console.WriteLine($"║  URL: {baseUrl}/mcp");
    Console.WriteLine("║ WARNING: THERE IS NO AUTHENTICATION ON THIS MCP! ║");    
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();
});

app.Run();

record GenerateRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("file_id")] string? FileId,
    [property: System.Text.Json.Serialization.JsonPropertyName("voice_id")] string? VoiceId,
    [property: System.Text.Json.Serialization.JsonPropertyName("resolution")] string? Resolution,
    [property: System.Text.Json.Serialization.JsonPropertyName("transition")] string? Transition
);
