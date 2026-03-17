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

// ElevenLabs HTTP client
builder.Services.AddHttpClient<ElevenLabsClient>(client =>
{
    client.BaseAddress = new Uri("https://api.elevenlabs.io/");
    client.DefaultRequestHeaders.Add("xi-api-key", options.ElevenLabsApiKey);
    client.Timeout = TimeSpan.FromSeconds(120);
});

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

// Simple REST endpoint for the web UI to load voices without MCP handshake
app.MapGet("/api/voices", (EchoDeckOptions opts) =>
{
    var voices = opts.ParseVoices().Select(v => new { id = v.Id, name = v.Name });
    return Results.Ok(new { voices });
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

app.Run();
