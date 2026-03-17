# EchoDeck — PowerPoint to Narrated Video

> Turn any .pptx with speaker notes into a professional narrated video, powered by ElevenLabs TTS and delivered as an MCP tool for Claude.

## Overview

EchoDeck is an open-source MCP server (C# / .NET 10) that accepts a PowerPoint file, extracts speaker notes, renders each slide via Microsoft Office Online (Playwright), generates voice narration via ElevenLabs, and stitches everything into an .mp4 using FFmpeg. It deploys to Railway in one click.

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                     Claude (MCP Client)                  │
│  "Generate a video from this deck in Sam's voice"        │
└──────────────┬───────────────────────────┬───────────────┘
               │ generate_video            │ get_job_status
               ▼                           ▼
┌──────────────────────────────────────────────────────────┐
│                   EchoDeck MCP Server                    │
│                  (.NET 10 / ASP.NET)                     │
│                                                          │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────┐  │
│  │   Slide      │  │   Audio      │  │   Video        │  │
│  │   Extractor  │→ │   Generator  │→ │   Assembler    │  │
│  │  (OpenXml)   │  │ (ElevenLabs) │  │   (FFmpeg)     │  │
│  └──────┬──────┘  └──────────────┘  └────────────────┘  │
│         │                                                │
│  ┌──────▼──────┐                                         │
│  │   Slide      │                                        │
│  │   Renderer   │                                        │
│  │ (Playwright) │                                        │
│  └─────────────┘                                         │
│                                                          │
│  ┌─────────────────────────────────────────────────┐     │
│  │  Test Mode Web UI (SignalR + drag-and-drop)     │     │
│  └─────────────────────────────────────────────────┘     │
└──────────────────────────────────────────────────────────┘
```

---

## Project Structure

```
EchoDeck/
├── src/
│   ├── EchoDeck.Core/                    # Shared pipeline logic (class library)
│   │   ├── Models/
│   │   │   ├── Slide.cs                  # Index, ImagePath, SpeakerNotes, AudioPath, Duration
│   │   │   ├── VideoJob.cs               # JobId, Status, Progress, Steps[], OutputPath, Error
│   │   │   └── EchoDeckOptions.cs        # Resolution, TransitionStyle, VoiceId, PaddingMs, etc.
│   │   ├── Pipeline/
│   │   │   ├── IPipelineStep.cs          # interface IPipelineStep { Task ExecuteAsync(VideoJob job, ...) }
│   │   │   ├── SlideExtractor.cs         # OpenXml: parse .pptx → list of Slide (notes + slide count)
│   │   │   ├── ISlideRenderer.cs         # interface ISlideRenderer { Task<List<string>> RenderAsync(...) }
│   │   │   ├── OfficeOnlineRenderer.cs   # Playwright: Office Online → screenshot each slide as PNG
│   │   │   ├── LibreOfficeRenderer.cs    # LibreOffice headless → export slides as PNG (fallback)
│   │   │   ├── MockSlideRenderer.cs      # Generates solid-color placeholder PNGs (local dev / CI)
│   │   │   ├── AudioGenerator.cs         # ElevenLabs API → mp3 per slide (parallel with SemaphoreSlim)
│   │   │   ├── VideoAssembler.cs         # FFmpeg: stitch slide PNGs + audio segments → final .mp4
│   │   │   └── PipelineOrchestrator.cs   # Runs all steps in order, emits progress events
│   │   └── Services/
│   │       ├── ElevenLabsClient.cs       # Typed HttpClient for ElevenLabs REST API
│   │       ├── FFmpegService.cs          # Wrapper around FFmpeg CLI (or FFMpegCore NuGet)
│   │       ├── TempFileServer.cs         # Serves .pptx at a temp public URL for Office Online
│   │       └── JobCleanupService.cs      # Background service: deletes job files older than JOB_RETENTION_HOURS
│   │
│   └── EchoDeck.Mcp/                     # MCP server + host (ASP.NET app)
│       ├── Program.cs                    # WebApplication builder, MCP registration, SignalR, etc.
│       ├── Tools/
│       │   ├── GenerateVideoTool.cs      # MCP tool: accepts .pptx (base64), voiceId → returns jobId
│       │   ├── ListVoicesTool.cs         # MCP tool: returns configured ElevenLabs voices
│       │   └── GetJobStatusTool.cs       # MCP tool: poll job by ID → status, progress, download URL
│       ├── Hubs/
│       │   └── ProgressHub.cs            # SignalR hub: pushes step-by-step progress to browser
│       ├── wwwroot/
│       │   └── index.html                # Single-file drag-and-drop UI with real-time progress
│       └── appsettings.json
│
├── tests/
│   ├── EchoDeck.Core.Tests/
│   │   ├── SlideExtractorTests.cs        # Unit tests with sample .pptx files
│   │   ├── VideoAssemblerTests.cs        # FFmpeg integration tests
│   │   └── PipelineOrchestratorTests.cs
│   └── fixtures/
│       └── sample.pptx                   # Test presentation with speaker notes
│
├── Dockerfile
├── railway.toml
├── .env.example
├── LICENSE                               # MIT
├── README.md
└── EchoDeck.sln
```

---

## Pipeline Detail

### Step 1: SlideExtractor (OpenXml)

**Input:** .pptx byte stream
**Output:** `List<Slide>` with speaker notes per slide

```csharp
// Pseudocode
using var doc = PresentationDocument.Open(stream, false);
var slideParts = doc.PresentationPart.SlideParts;

foreach (var (slidePart, index) in slideParts.Select((sp, i) => (sp, i)))
{
    var notes = slidePart.NotesSlidePart?.NotesSlide?.InnerText?.Trim() ?? "";
    slides.Add(new Slide { Index = index, SpeakerNotes = notes });
}
```

**Edge cases to handle:**
- Slides with empty speaker notes → flag with a warning, include in job status so Claude can ask the user
- Slides with very long notes → warn about ElevenLabs character limits (5000 chars/request), split if needed
- Slide ordering must follow the presentation order in the XML, not file system order

**NuGet:** `DocumentFormat.OpenXml`

---

### Step 2: SlideRenderer (Playwright + Office Online)

**Input:** .pptx file path, slide count
**Output:** PNG screenshot per slide saved to temp directory

#### How it works

1. **Serve the .pptx temporarily.** The service exposes a temporary authenticated endpoint:
   ```
   GET /temp/{jobId}/{filename}.pptx
   ```
   This URL must be publicly reachable (Railway gives you `RAILWAY_PUBLIC_DOMAIN`). The file is deleted after rendering completes.

2. **Open in Office Online embed viewer.** Playwright navigates to:
   ```
   https://view.officeapps.live.com/op/embed.aspx?src={url-encoded-public-url}
   ```

3. **Screenshot each slide.** The embed viewer renders one slide at a time. For each slide:
   - Wait for the slide content to fully render (wait for loading spinners to disappear)
   - Screenshot the slide viewport area (not the full page — crop to the slide itself)
   - Click the "Next Slide" button / arrow key to advance
   - Wait for transition to settle before next screenshot

4. **Clean up.** Delete the temp file route.

#### Key implementation details

- **Viewport size:** Set Playwright viewport to match target resolution (default 1920x1080)
- **Selectors:** You'll need to inspect the Office Online embed viewer to find the correct selectors for:
  - The slide rendering area (to crop screenshots)
  - The "next slide" navigation control
  - Loading/spinner indicators
  - The current slide number (to verify navigation)
- **Timeouts:** Office Online can be slow. Use generous timeouts (30s per slide) with retry logic.
- **Headless mode:** Run Chromium headless in Docker. Use `--no-sandbox` flag in container.

#### Early validation / debugging

Office Online may block headless server-side requests — this is uncertain and worth trying, but must be validated early. On startup (as part of the `/health` check), the server renders a known embedded test slide and verifies it produces a valid screenshot. If this fails, the health check logs a clear diagnostic and returns a degraded status so the problem is caught before any user job is attempted.

#### Fallback / risk mitigation

Office Online requires the file to be publicly accessible via HTTPS. If this proves unreliable:
- **Fallback A:** LibreOffice headless (install MS core fonts in Docker for better fidelity: `ttf-mscorefonts-installer`)
- **Fallback B:** Upload to OneDrive via MS Graph API, use their export-to-image endpoint

Make the renderer pluggable via an `ISlideRenderer` interface so users can swap strategies via config (`SLIDE_RENDERER=officeOnline|libreOffice`).

**NuGet:** `Microsoft.Playwright`

---

### Step 3: AudioGenerator (ElevenLabs)

**Input:** `List<Slide>` with speaker notes
**Output:** mp3 file per slide, duration metadata

```csharp
// Pseudocode — parallel with rate limiting
var semaphore = new SemaphoreSlim(3); // max 3 concurrent requests

var tasks = slides.Where(s => !string.IsNullOrEmpty(s.SpeakerNotes)).Select(async slide =>
{
    await semaphore.WaitAsync();
    try
    {
        var audioBytes = await elevenLabsClient.TextToSpeechAsync(voiceId, slide.SpeakerNotes);
        slide.AudioPath = Path.Combine(tempDir, $"slide_{slide.Index}.mp3");
        await File.WriteAllBytesAsync(slide.AudioPath, audioBytes);
        slide.Duration = await ffmpegService.GetDurationAsync(slide.AudioPath);
    }
    finally { semaphore.Release(); }
});

await Task.WhenAll(tasks);
```

#### ElevenLabs API integration

```
POST https://api.elevenlabs.io/v1/text-to-speech/{voice_id}
Headers:
  xi-api-key: {ELEVENLABS_API_KEY}
  Content-Type: application/json
  Accept: audio/mpeg
Body:
  {
    "text": "speaker notes text here",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": { "stability": 0.5, "similarity_boost": 0.75 }
  }
```

#### Edge cases
- Slides with no speaker notes → generate a short silence (e.g., 2 seconds) using FFmpeg instead of calling ElevenLabs
- Very long notes → split at sentence boundaries, generate multiple audio chunks, concatenate
- Rate limit errors (429) → exponential backoff with jitter, max 3 retries

---

### Step 4: VideoAssembler (FFmpeg)

**Input:** Slide PNGs + audio mp3s with durations
**Output:** Single .mp4 file

#### Per-slide segment creation

```bash
ffmpeg -loop 1 -i slide_0.png -i slide_0.mp3 \
  -c:v libx264 -tune stillimage -c:a aac \
  -pix_fmt yuv420p -shortest \
  -vf "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2" \
  -y segment_0.mp4
```

For slides with no audio (silent slides):
```bash
ffmpeg -loop 1 -i slide_5.png -t 3 \
  -c:v libx264 -tune stillimage -pix_fmt yuv420p \
  -y segment_5.mp4
```

#### Concatenation

Create a concat file:
```
file 'segment_0.mp4'
file 'segment_1.mp4'
file 'segment_2.mp4'
```

```bash
ffmpeg -f concat -safe 0 -i concat.txt -c copy output.mp4
```

#### Optional transitions (crossfade)

If `TRANSITION_STYLE=crossfade`:
```bash
ffmpeg -i segment_0.mp4 -i segment_1.mp4 \
  -filter_complex "xfade=transition=fade:duration=0.5:offset={segment0_duration - 0.5}" \
  -y merged_01.mp4
```

This gets complex with many slides — chain pairwise or use a filter graph.

#### Post-processing
- Add 0.5s padding of silence at the start and end of the video
- Normalize audio levels across all segments
- Ensure consistent frame rate (30fps)

**NuGet:** `FFMpegCore` or raw `Process.Start("ffmpeg", args)`

---

### Step 5: Return result via MCP

The `generate_video` tool returns a job ID immediately. Claude polls `get_job_status` which returns:

```json
{
  "jobId": "abc-123",
  "status": "completed",
  "progress": 100,
  "steps": [
    { "name": "extract_slides", "status": "completed", "detail": "12 slides found" },
    { "name": "render_slides", "status": "completed", "detail": "12/12 screenshots" },
    { "name": "generate_audio", "status": "completed", "detail": "11/12 (1 slide silent)" },
    { "name": "assemble_video", "status": "completed", "detail": "2m 34s final duration" }
  ],
  "outputUrl": "/jobs/abc-123/output.mp4",
  "warnings": ["Slide 7 has no speaker notes — used 2s silence"]
}
```

The .mp4 is stored on the Railway mounted volume under `/data/jobs/{jobId}/output.mp4`. The MCP response returns a download URL pointing to a static file endpoint on the server. The user (who runs their own Railway deploy) downloads directly from their own instance. Claude includes the URL and a reminder that the file will be cleaned up after `JOB_RETENTION_HOURS` hours.

---

## MCP Tool Definitions

### generate_video

```
Name: generate_video
Description: Generate a narrated video from a PowerPoint presentation.
             The .pptx must have speaker notes on slides to narrate.
Parameters:
  - pptx_base64 (string, required): Base64-encoded .pptx file
  - voice_id (string, optional): ElevenLabs voice ID. Use list_voices to see options.
                                  Defaults to the first configured voice.
  - resolution (string, optional): "1920x1080" (default), "1280x720", "3840x2160"
  - transition (string, optional): "none" (default), "crossfade"
Returns:
  - job_id (string): Use with get_job_status to poll progress
```

### list_voices

```
Name: list_voices
Description: List available ElevenLabs voices for video narration.
Parameters: none
Returns:
  - voices: [{ "id": "abc123", "name": "Sam" }, ...]
```

### get_job_status

```
Name: get_job_status
Description: Check the status of a video generation job.
Parameters:
  - job_id (string, required)
Returns:
  - status: "queued" | "processing" | "completed" | "failed"
  - progress: 0-100
  - steps: [{ name, status, detail }]
  - output_base64: (only when completed) base64-encoded .mp4
  - warnings: string[]
  - error: string (only when failed)
```

---

## Test Mode Web UI

When `TEST_MODE=true`, an additional web interface is served at the root URL.

### Features
- **Drag-and-drop zone** for .pptx files
- **Voice selector** dropdown (populated from configured voices)
- **Real-time progress** via SignalR WebSocket connection:
  - Step-by-step status updates
  - Thumbnail previews of each slide as they're captured
  - Audio preview (play button) for each generated segment
  - Overall progress bar
- **Video preview** — embedded `<video>` player when the job completes
- **Download button** for the final .mp4
- **Debug log panel** — raw pipeline logs streamed in real time

### Tech
- Single `index.html` file with inline CSS/JS (no build step)
- SignalR JavaScript client from CDN
- The `PipelineOrchestrator` emits events via `IProgress<T>` which `ProgressHub` forwards to connected clients

---

## Environment Variables / Configuration

| Variable | Required | Default | Description |
|---|---|---|---|
| `ELEVENLABS_API_KEY` | Yes | — | ElevenLabs API key for TTS |
| `ELEVENLABS_VOICES` | Yes | — | Comma-separated `Name:voice_id` pairs, e.g. `Sam:abc123,Rachel:def456` |
| `MCP_AUTH_KEY` | Yes | — | Shared secret for MCP client authentication |
| `TEST_MODE` | No | `false` | Set to `true` to enable the debug web UI |
| `BASE_URL` | No | auto-detected | Public URL of this service (Railway sets `RAILWAY_PUBLIC_DOMAIN`) |
| `DEFAULT_RESOLUTION` | No | `1920x1080` | Default video resolution |
| `DEFAULT_TRANSITION` | No | `none` | Default transition style: `none`, `crossfade` |
| `SLIDE_PADDING_MS` | No | `500` | Extra silence (ms) after each slide's audio |
| `MAX_CONCURRENT_TTS` | No | `3` | Max parallel ElevenLabs API requests |
| `JOB_RETENTION_HOURS` | No | `24` | How long to keep completed job files before cleanup |
| `DATA_DIR` | No | `./data` | Root path for job output storage; set to `/data` on Railway where a volume is mounted |
| `SLIDE_RENDERER` | No | `officeOnline` | Slide rendering strategy: `officeOnline`, `libreOffice`, `mock` |

---

## Local Development (Windows)

### Prerequisites

```
winget install ffmpeg
winget install cloudflare.cloudflared   # only needed to test Office Online integration
```

Playwright browser binaries are downloaded automatically on first run via the NuGet package.

### `appsettings.Development.json` defaults

```json
{
  "EchoDeck": {
    "DataDir": "./data",
    "SlideRenderer": "mock"
  }
}
```

`DATA_DIR` defaults to `./data` (relative to the running app), so no path config is needed for local dev. All pipeline output lands in `src/EchoDeck.Mcp/data/` during a `dotnet run`.

### Recommended local dev workflow

| What you're testing | `SLIDE_RENDERER` | Extra setup |
|---|---|---|
| Everything except rendering (audio, FFmpeg, MCP, UI) | `mock` | None — placeholder PNGs generated in-process |
| LibreOffice rendering | `libreOffice` | Install LibreOffice; set `LIBREOFFICE_PATH` if not on `PATH` |
| Office Online rendering | `officeOnline` | Run `cloudflared tunnel --url http://localhost:8080`, set `BASE_URL` to the tunnel URL |

### Cross-platform notes

- **File paths** — always use `Path.Combine` (never hardcoded slashes). `DATA_DIR` defaults differ: `./data` locally, `/data` on Railway.
- **FFmpeg binary** — call as `ffmpeg` in `Process.Start`; .NET resolves `.exe` on Windows automatically.
- **LibreOffice binary path** — differs by platform (`soffice.exe` on Windows, `libreoffice` on Linux). Resolve via a `LIBREOFFICE_PATH` env var with a per-platform fallback using `RuntimeInformation.IsOSPlatform`.
- **Startup health check** — skips the Office Online render test when `SLIDE_RENDERER` is `mock` or `libreOffice`.

---

## Dockerfile

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY EchoDeck.sln .
COPY src/EchoDeck.Core/*.csproj src/EchoDeck.Core/
COPY src/EchoDeck.Mcp/*.csproj src/EchoDeck.Mcp/
RUN dotnet restore
COPY . .
RUN dotnet publish src/EchoDeck.Mcp -c Release -o /app

# Runtime stage — Playwright base image has Chromium pre-installed
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-noble AS runtime
WORKDIR /app
COPY --from=build /app .

# Install FFmpeg
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "EchoDeck.Mcp.dll"]
```

---

## railway.toml

```toml
[build]
builder = "dockerfile"
dockerfilePath = "Dockerfile"

[deploy]
startCommand = "dotnet EchoDeck.Mcp.dll"
healthcheckPath = "/health"
healthcheckTimeout = 300
restartPolicyType = "on_failure"
restartPolicyMaxRetries = 3

[service]
internalPort = 8080

[[mounts]]
mountPath = "/data"
```

> **Note:** After deploying to Railway, add a volume via the Railway dashboard and mount it at `/data`. This is where all job output files (slide PNGs, audio segments, final .mp4) are stored. The `JobCleanupService` background service sweeps this directory on a timer and deletes job folders older than `JOB_RETENTION_HOURS` (default 24h).

---

## Build Order (suggested implementation sequence)

### Phase 1: Core pipeline with placeholder images
1. **Solution scaffold** — create .sln, projects, folder structure, NuGet references
2. **Models** — `Slide.cs`, `VideoJob.cs`, `EchoDeckOptions.cs`
3. **SlideExtractor** — parse .pptx with OpenXml, extract speaker notes, unit tests with a sample .pptx
4. **ElevenLabsClient** — typed HttpClient, TTS endpoint, error handling, retry logic
5. **AudioGenerator** — parallel audio generation with rate limiting
6. **FFmpegService** — wrapper for duration detection and encoding commands
7. **VideoAssembler** — stitch placeholder colored slides + audio → .mp4 (proves the FFmpeg pipeline works end-to-end without needing Playwright yet)
8. **PipelineOrchestrator** — wire steps together with progress reporting

### Phase 2: Slide rendering
9. **`ISlideRenderer` interface + `MockSlideRenderer`** — solid-color PNGs with slide number; validates the rest of the pipeline works before real rendering is attempted
10. **`LibreOfficeRenderer`** — headless LibreOffice export; useful as a locally testable real renderer before tackling Office Online
11. **TempFileServer** — middleware to serve .pptx at a temporary public URL
12. **`OfficeOnlineRenderer`** — Playwright + Office Online integration
    - Validate locally using a `cloudflared` tunnel
    - Inspect the embed viewer, identify selectors
    - Handle navigation between slides
    - Screenshot cropping to slide viewport
    - Retry logic for flaky loads

### Phase 3: MCP integration
12. **MCP server setup** — register tools, configure auth
13. **GenerateVideoTool** — accept base64 .pptx, kick off pipeline, return job ID
14. **ListVoicesTool** — return configured voices from env
15. **GetJobStatusTool** — return job progress and completed .mp4
16. **Job management** — in-memory job store, cleanup old jobs on a timer

### Phase 4: Test mode UI
17. **ProgressHub** — SignalR hub inside `EchoDeck.Mcp` that forwards pipeline events to browser
18. **index.html** — drag-and-drop UI with real-time progress, video preview, download (served from `wwwroot/`)
19. **Conditional startup** — only mount web UI routes when `TEST_MODE=true`

### Phase 5: Deployment
20. **JobCleanupService** — `IHostedService` that sweeps `/data/jobs/` on a timer and deletes folders older than `JOB_RETENTION_HOURS`
21. **Dockerfile** — multi-stage build, Playwright base image, FFmpeg
22. **railway.toml** — deploy config with `/data` volume mount
23. **.env.example** — document all env vars
24. **README.md** — setup guide, "Deploy to Railway" button, volume setup instructions, usage with Claude
25. **Railway template** — publish to Railway's template marketplace

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Office Online blocks server-side requests | Rendering fails entirely | Startup health check renders a known test slide — fails fast with clear diagnostic; `ISlideRenderer` interface lets user switch to `libreOffice` via env var |
| Office Online embed viewer changes selectors | Screenshots break silently | Same startup health check catches selector drift before any real job runs |
| .pptx must be publicly accessible for Office Online | Exposure window for sensitive decks | Temp URL uses random UUID path and is deleted immediately after render; acceptable since this is a single-user self-hosted deploy |
| ElevenLabs rate limits (free tier: limited chars/month) | Audio generation fails mid-job | SemaphoreSlim, exponential backoff, clear error messages with remaining quota |
| Large presentations (50+ slides) timeout | Job never completes | Set per-step timeouts; stream progress so user sees it's working; consider chunked processing |
| FFmpeg not available or wrong version | Video assembly fails | Pin FFmpeg version in Dockerfile; verify on startup with `ffmpeg -version` health check |
| Playwright browser crashes in container | Rendering hangs | Per-slide timeout, browser restart logic, `--no-sandbox --disable-dev-shm-usage` flags |

---

## Future Enhancements (post-MVP)

- **Subtitle track (.srt)** — generated almost for free since we already have text + timing data
- **Background music** — mix in a low-volume background track under the narration
- **Slide animations** — detect animations in .pptx and add motion (ambitious)
- **Custom intro/outro slides** — prepend/append title cards
- **Batch processing** — queue multiple presentations
- **Webhook notifications** — POST to a URL when job completes
- **MS Graph renderer** — highest fidelity option for users with Microsoft 365
- **Voice cloning** — let users upload a voice sample to ElevenLabs and use it
- **Caching** — hash speaker notes + voice ID, skip re-generating identical audio
- **Progress estimation** — predict remaining time based on slide count and average per-slide duration
