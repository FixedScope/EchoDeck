namespace EchoDeck.Core.Models;

public class ProgressEvent
{
    public string JobId { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string StepStatus { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public int Progress { get; set; }
    public string? ThumbnailBase64 { get; set; } // for slide render previews
    public string? AudioUrl { get; set; } // for audio previews
}
