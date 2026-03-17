using EchoDeck.Core.Pipeline;

namespace EchoDeck.Core.Tests;

public class MockSlideRendererTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly MockSlideRenderer _renderer = new();

    [Fact]
    public async Task RenderAsync_ProducesOnePngPerSlide()
    {
        var paths = await _renderer.RenderAsync("fake.pptx", 3, _tempDir);

        Assert.Equal(3, paths.Count);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.All(paths, p => Assert.Equal(".png", Path.GetExtension(p)));
    }

    [Fact]
    public async Task HealthCheck_AlwaysReturnsNull()
    {
        var result = await _renderer.HealthCheckAsync();
        Assert.Null(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
