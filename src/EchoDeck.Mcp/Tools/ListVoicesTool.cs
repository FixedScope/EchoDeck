using EchoDeck.Core.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EchoDeck.Mcp.Tools;

[McpServerToolType]
public static class ListVoicesTool
{
    [McpServerTool(Name = "list_voices"), Description("List available ElevenLabs voices configured for video narration.")]
    public static object ListVoices(EchoDeckOptions options)
    {
        var voices = options.ParseVoices()
            .Select(v => new { id = v.Id, name = v.Name })
            .ToList();

        return new { voices };
    }
}
