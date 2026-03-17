using EchoDeck.Core.Models;

namespace EchoDeck.Core.Pipeline;

public interface ISlideRenderer
{
    /// <summary>
    /// Renders each slide as a PNG and returns the list of file paths, one per slide.
    /// </summary>
    Task<List<string>> RenderAsync(string pptxPath, int slideCount, string outputDir, CancellationToken ct = default);

    /// <summary>
    /// Called on startup to validate this renderer is functional.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    Task<string?> HealthCheckAsync(CancellationToken ct = default);
}
