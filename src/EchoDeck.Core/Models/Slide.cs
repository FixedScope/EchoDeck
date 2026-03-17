namespace EchoDeck.Core.Models;

public class Slide
{
    public int Index { get; set; }
    public string SpeakerNotes { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public string? AudioPath { get; set; }
    public double DurationSeconds { get; set; }
    public bool IsSilent => string.IsNullOrWhiteSpace(SpeakerNotes);
}
