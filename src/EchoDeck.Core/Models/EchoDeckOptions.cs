namespace EchoDeck.Core.Models;

public class EchoDeckOptions
{
    public const string SectionName = "EchoDeck";

    public string DataDir { get; set; } = "./data";
    public string SlideRenderer { get; set; } = "officeOnline"; // officeOnline, libreOffice, mock
    public string DefaultResolution { get; set; } = "1920x1080";
    public string DefaultTransition { get; set; } = "crossfade"; // none, crossfade
    public int SlidePaddingMs { get; set; } = 500;
    public int MaxConcurrentTts { get; set; } = 3;
    public int JobRetentionHours { get; set; } = 24;
    public string BaseUrl { get; set; } = string.Empty;
    public string ElevenLabsApiKey { get; set; } = string.Empty;
    public string ElevenLabsVoices { get; set; } = string.Empty; // "Sam:abc123,Rachel:def456"
    public string GeminiApiKey { get; set; } = string.Empty;
    public string McpAuthKey { get; set; } = string.Empty;
    public bool TestMode { get; set; } = false;
    public string LibreOfficePath { get; set; } = string.Empty;

    public (int Width, int Height) ParseResolution()
    {
        var parts = DefaultResolution.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            ? (w, h)
            : (1920, 1080);
    }

    public List<(string Name, string Id)> ParseVoices()
    {
        if (string.IsNullOrWhiteSpace(ElevenLabsVoices)) return new();
        return ElevenLabsVoices.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v =>
            {
                var parts = v.Trim().Split(':', 2);
                return parts.Length == 2 ? (parts[0].Trim(), parts[1].Trim()) : (v.Trim(), v.Trim());
            })
            .ToList();
    }

    public string GetJobDir(string jobId) =>
        Path.Combine(DataDir, "jobs", jobId);
}
