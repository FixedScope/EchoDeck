namespace EchoDeck.Core.Models;

public class JobStep
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "pending"; // pending, running, completed, failed
    public string Detail { get; set; } = string.Empty;
}
