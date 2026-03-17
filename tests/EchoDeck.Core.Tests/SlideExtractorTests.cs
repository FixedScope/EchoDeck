using EchoDeck.Core.Pipeline;

namespace EchoDeck.Core.Tests;

public class SlideExtractorTests
{
    private readonly SlideExtractor _extractor = new();

    [Fact]
    public void Extract_WithValidPptx_ReturnsSlides()
    {
        // This test requires a sample.pptx fixture. Skipped if not present.
        var assemblyDir = Path.GetDirectoryName(typeof(SlideExtractorTests).Assembly.Location)!;
        var fixturePath = Path.Combine(assemblyDir, "fixtures", "sample.pptx");
        if (!File.Exists(fixturePath))
        {
            // Skip gracefully — CI will add the fixture
            return;
        }

        using var stream = File.OpenRead(fixturePath);
        var slides = _extractor.Extract(stream);

        Assert.NotEmpty(slides);
        Assert.All(slides, s => Assert.True(s.Index >= 0));
    }

    [Fact]
    public void Extract_WithInvalidStream_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 0x00, 0x01, 0x02 });
        Assert.ThrowsAny<Exception>(() => _extractor.Extract(stream));
    }
}
