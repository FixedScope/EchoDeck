# EchoDeck

Convert PowerPoint presentations into narrated MP4 videos using ElevenLabs or Google Gemini TTS — powered by an MCP server you can connect directly to Claude.

[![Deploy on Railway](https://railway.com/button.svg)](https://railway.com/deploy/jgi04x?referralCode=7Y_8ni&utm_medium=integration&utm_source=template&utm_campaign=generic)

---

## How it works

1. Drop a `.pptx` file into your Claude chat (or provide a path)
2. Claude calls `get_upload_slot` to get a curl command, uploads your file
3. Claude calls `generate_video` with your chosen voice
4. FFmpeg assembles slides + audio into a narrated MP4
5. Claude returns a download link when the job completes

Speaker notes on each slide become the narration. Slides with no notes get a 3-second pause.

---

## Prerequisites

- **ElevenLabs** (required) — [sign up at elevenlabs.io](https://elevenlabs.io) — high-quality, cloned voices. Requires an API key and at least one voice ID.
- **Google Gemini** (optional) — [get a key at aistudio.google.com](https://aistudio.google.com) — adds 30 built-in voices on top of ElevenLabs. Requires a Tier 1 (paid) account for reasonable throughput.

---

## Deploy on Railway

1. Click the **Deploy on Railway** button above
2. Set `ELEVENLABS_API_KEY` and `ELEVENLABS_VOICES` (required)
3. Optionally set `GEMINI_API_KEY` to also enable Gemini voices
4. Add a Railway Volume mounted at `/data` (Service → Volumes → Add Volume)
5. Deploy — Railway will build the Docker image and run the health check

---

## Environment Variables

| Variable | Required | Default | Description |
|---|---|---|---|
| `ELEVENLABS_API_KEY` | Yes | — | Your ElevenLabs API key |
| `ELEVENLABS_VOICES` | Yes | — | Comma-separated `Name:voice_id` pairs, e.g. `Sam:abc123,Rachel:def456` |
| `GEMINI_API_KEY` | No | — | Your Google Gemini API key — adds 30 built-in voices |
| `MCP_AUTH_KEY` | Auto-generated | — | Secret key for MCP access (auto-set by Railway template) |
| `TEST_MODE` | No | `false` | Set to `true` to enable the web UI at the root URL |
| `DATA_DIR` | No | `/data` | Directory for job storage — mount a Railway Volume here |
| `SLIDE_RENDERER` | No | `officeOnline` | `officeOnline`, `libreOffice`, or `mock` |
| `DEFAULT_RESOLUTION` | No | `1920x1080` | Output video resolution (`1280x720`, `1920x1080`, `3840x2160`) |
| `DEFAULT_TRANSITION` | No | `crossfade` | Transition between slides: `none` or `crossfade` |
| `JOB_RETENTION_HOURS` | No | `24` | How long to keep completed job files |
| `MAX_CONCURRENT_TTS` | No | `3` | Max parallel TTS requests (reduce to `1` on free-tier Gemini) |

---

## Connect to Claude Desktop

1. Open Claude Desktop → **Settings** → **Integrations** → **Custom Connectors**
2. Add a new connector:
   - **Name:** EchoDeck
   - **URL:** `https://your-app.railway.app/mcp`
3. Authenticate when prompted (uses `MCP_AUTH_KEY`)
4. EchoDeck tools will now be available in your Claude chats

---

## MCP Tools

| Tool | Description |
|---|---|
| `list_voices` | List all available TTS voices (ElevenLabs + Gemini) |
| `get_upload_slot` | Get the upload URL and a ready-to-run curl command |
| `generate_video` | Start a video generation job from an uploaded file |
| `get_job_status` | Poll a job for progress and retrieve the download URL |

### Example conversation

```
You: Generate a video from this presentation using a Gemini voice
Claude: Let me list the available voices first...
        [calls list_voices]
        I'll use "gemini:Kore". First let me get the upload URL...
        [calls get_upload_slot]
        Please run this command:
          curl -X POST https://your-app.railway.app/upload/pptx -F "file=@presentation.pptx"
        Then paste the file_id back.

You: file_id is abc123def456
Claude: [calls generate_video with file_id and voice_id]
        Job started! Polling for progress...
        [calls get_job_status periodically]
        Done! Download your video: https://your-app.railway.app/jobs-data/...
```

---

## Test Mode Web UI

Set `TEST_MODE=true` to enable a browser-based UI at your app's root URL. It lets you upload a PPTX, pick a voice, and watch real-time progress via SignalR — useful for testing without Claude.

---

## Self-hosting notes

- **Slide renderer:** The default `officeOnline` mode renders slides via Microsoft Office Online (requires the PPTX to be publicly accessible via the `/temp/` endpoint). Use `mock` for testing or `libreOffice` if you have LibreOffice installed in your environment.
- **Storage:** Job files (slides, audio, final MP4) are stored under `DATA_DIR`. Mount a Railway Volume at `/data` so files survive redeploys.
- **Job cleanup:** Completed jobs are automatically deleted after `JOB_RETENTION_HOURS` (default 24h).

---

## License

MIT
