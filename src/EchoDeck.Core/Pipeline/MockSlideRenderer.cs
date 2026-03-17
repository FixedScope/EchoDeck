using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EchoDeck.Core.Pipeline;

public class MockSlideRenderer : ISlideRenderer
{
    private static readonly Rgba32[] SlideColors =
    [
        new Rgba32(74, 144, 217),
        new Rgba32(230, 126, 34),
        new Rgba32(46, 204, 113),
        new Rgba32(155, 89, 182),
        new Rgba32(231, 76, 60),
        new Rgba32(26, 188, 156),
    ];

    public async Task<List<string>> RenderAsync(string pptxPath, int slideCount, string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var paths = new List<string>();

        for (int i = 0; i < slideCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var color = SlideColors[i % SlideColors.Length];
            var path = Path.Combine(outputDir, $"slide_{i:D3}.png");
            await GeneratePlaceholderAsync(color, path);
            paths.Add(path);
        }

        return paths;
    }

    public Task<string?> HealthCheckAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    private static Task GeneratePlaceholderAsync(Rgba32 bgColor, string outputPath)
    {
        using var image = new Image<Rgba32>(1920, 1080, bgColor);
        image.SaveAsPng(outputPath);
        return Task.CompletedTask;
    }
}
