using EchoDeck.Core.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EchoDeck.Mcp.Tools;

[McpServerToolType]
public static class GetUploadSlotTool
{
    [McpServerTool(Name = "get_upload_slot"), Description(
        "Call this before generate_video to get a URL to upload a .pptx file. " +
        "Returns an upload_url and the exact curl command to run. " +
        "Run the curl command with the actual file path to get a file_id, then pass that file_id to generate_video. " +
        "Example flow: 1) call get_upload_slot, 2) run the curl command replacing /path/to/file.pptx, 3) call generate_video with the returned file_id.")]
    public static object GetUploadSlot(EchoDeckOptions options)
    {
        var uploadUrl = $"{options.BaseUrl.TrimEnd('/')}/upload/pptx";
        return new
        {
            upload_url = uploadUrl,
            curl_command = $"curl -X POST \"{uploadUrl}\" -F \"file=@/path/to/file.pptx\"",
            instructions = "Replace /path/to/file.pptx with the actual file path, run the curl command, then pass the returned file_id to generate_video.",
        };
    }
}
