namespace EchoDeck.Core.Services;

/// <summary>
/// Tracks temporary .pptx files that need to be served publicly for Office Online rendering.
/// The actual HTTP endpoint is registered in EchoDeck.Mcp.
/// </summary>
public class TempFileServer
{
    private readonly Dictionary<string, string> _files = new(); // key: "jobId/filename", value: file path
    private readonly object _lock = new();

    public string Register(string jobId, string filePath)
    {
        var key = $"{jobId}/{Path.GetFileName(filePath)}";
        lock (_lock) { _files[key] = filePath; }
        return key;
    }

    public string? GetFilePath(string key)
    {
        lock (_lock) { return _files.TryGetValue(key, out var path) ? path : null; }
    }

    public void Unregister(string jobId)
    {
        lock (_lock)
        {
            var keysToRemove = _files.Keys.Where(k => k.StartsWith($"{jobId}/")).ToList();
            foreach (var k in keysToRemove) _files.Remove(k);
        }
    }
}
