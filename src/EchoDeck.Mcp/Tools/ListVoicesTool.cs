using EchoDeck.Core.Models;
using EchoDeck.Core.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EchoDeck.Mcp.Tools;

[McpServerToolType]
public static class ListVoicesTool
{
    [McpServerTool(Name = "list_voices"), Description("List available TTS voices for video narration. Returns ElevenLabs voices (if configured) and Gemini voices (if GEMINI_API_KEY is set).")]
    public static object ListVoices(EchoDeckOptions options)
    {
        var voices = new List<object>();

        if (!string.IsNullOrEmpty(options.ElevenLabsApiKey))
        {
            voices.AddRange(options.ParseVoices()
                .Select(v => (object)new { id = v.Id, name = v.Name, provider = "elevenlabs" }));
        }

        if (!string.IsNullOrEmpty(options.GeminiApiKey))
        {
            voices.AddRange(GeminiTtsClient.KnownVoices
                .Select(name => (object)new { id = $"gemini:{name}", name, provider = "gemini" }));
        }

        return new { voices };
    }
}
