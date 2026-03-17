namespace EchoDeck.Core.Models;

public class VideoJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Progress { get; set; }
    public List<JobStep> Steps { get; set; } = new()
    {
        new JobStep { Name = "extract_slides" },
        new JobStep { Name = "render_slides" },
        new JobStep { Name = "generate_audio" },
        new JobStep { Name = "assemble_video" },
    };
    public string? OutputPath { get; set; }
    public string? OutputUrl { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<Slide> Slides { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public JobStep GetStep(string name) => Steps.First(s => s.Name == name);
}
