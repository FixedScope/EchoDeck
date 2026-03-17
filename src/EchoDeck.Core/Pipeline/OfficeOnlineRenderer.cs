using Microsoft.Playwright;

namespace EchoDeck.Core.Pipeline;

/// <summary>
/// Renders slides using the Office Online embed viewer via Playwright.
/// Requires the .pptx to be served at a publicly accessible URL (BASE_URL).
///
/// DOM structure (verified 2026-03-16):
///   Outer page → #wacframe (iframe) → pus4-powerpoint.officeapps.live.com
///   Inside iframe: canvas (slide render surface), #SlidePanel (container)
///   Navigation: #ButtonFastFwd-Small14 (next), #SlideLabel-Medium14 (current slide number)
/// </summary>
public class OfficeOnlineRenderer(string tempBaseUrl) : ISlideRenderer, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    // Selectors (all inside the #wacframe child iframe)
    private const string SlideCanvasSelector  = "canvas";
    private const string SlidePanelSelector   = "#SlidePanel";
    private const string NextButtonSelector   = "#ButtonFastFwd-Small14";
    private const string SlideLabelSelector   = "#SlideLabel-Medium14";

    public async Task<List<string>> RenderAsync(
        string pptxPath,
        int slideCount,
        string outputDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        await EnsureBrowserAsync();

        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
        });

        try
        {
            var page = await context.NewPageAsync();

            var jobId   = Path.GetFileName(Path.GetDirectoryName(pptxPath)) ?? "unknown";
            var fileName = Path.GetFileName(pptxPath);
            var pptxUrl  = $"{tempBaseUrl.TrimEnd('/')}/temp/{jobId}/{fileName}";
            var embedUrl = $"https://view.officeapps.live.com/op/embed.aspx?src={Uri.EscapeDataString(pptxUrl)}";

            await page.GotoAsync(embedUrl, new PageGotoOptions { Timeout = 60_000 });

            // Wait for the wacframe iframe to appear in the outer page
            await page.WaitForSelectorAsync("#wacframe", new PageWaitForSelectorOptions { Timeout = 60_000 });

            // Locate the child frame
            var wacFrame = await WaitForFrameAsync(page, "wacframe", TimeSpan.FromSeconds(30), ct);

            // Wait for the slide canvas to be ready inside the iframe
            await wacFrame.WaitForSelectorAsync(SlidePanelSelector,
                new FrameWaitForSelectorOptions { Timeout = 60_000 });

            var paths = new List<string>();

            for (int i = 0; i < slideCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Wait for the slide label to show the expected slide number
                await WaitForSlideNumberAsync(wacFrame, i + 1, TimeSpan.FromSeconds(15));

                // Brief stabilization — canvas renders asynchronously
                await Task.Delay(800, ct);

                // Screenshot the canvas (the actual slide render surface)
                var screenshotPath = Path.Combine(outputDir, $"slide_{i:D3}.png");
                var canvas = await wacFrame.QuerySelectorAsync(SlideCanvasSelector);

                if (canvas != null)
                {
                    await canvas.ScreenshotAsync(new ElementHandleScreenshotOptions
                    {
                        Path = screenshotPath,
                        Type = ScreenshotType.Png,
                    });
                }
                else
                {
                    // Fallback: screenshot the whole iframe viewport
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = screenshotPath,
                        Type = ScreenshotType.Png,
                        Clip = new Clip { X = 0, Y = 0, Width = 1920, Height = 1080 },
                    });
                }

                paths.Add(screenshotPath);

                // Advance to next slide (skip on last)
                if (i < slideCount - 1)
                {
                    var nextBtn = await wacFrame.QuerySelectorAsync(NextButtonSelector);
                    if (nextBtn != null)
                        await nextBtn.ClickAsync();
                    else
                        await page.Keyboard.PressAsync("ArrowRight");

                    await Task.Delay(500, ct); // let the transition start
                }
            }

            return paths;
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    public async Task<string?> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureBrowserAsync();
            var context = await _browser!.NewContextAsync();
            var page = await context.NewPageAsync();

            // Just verify Office Online is reachable and returns a valid page
            var response = await page.GotoAsync(
                "https://view.officeapps.live.com/op/embed.aspx",
                new PageGotoOptions { Timeout = 30_000 });
            await context.CloseAsync();

            if (response == null || response.Status >= 500)
                return $"Office Online returned HTTP {response?.Status}";

            return null;
        }
        catch (Exception ex)
        {
            return $"Office Online health check failed: {ex.Message}. " +
                   "Consider switching SLIDE_RENDERER to 'libreOffice' or 'mock'.";
        }
    }

    /// <summary>
    /// Waits for the named iframe to appear and its content to start loading.
    /// </summary>
    private static async Task<IFrame> WaitForFrameAsync(
        IPage page, string frameName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var frame = page.Frames.FirstOrDefault(f => f.Name == frameName);
            if (frame != null && !string.IsNullOrEmpty(frame.Url) && frame.Url != "about:blank")
                return frame;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Frame '{frameName}' did not appear within {timeout.TotalSeconds}s.");
    }

    /// <summary>
    /// Waits for the slide label inside the iframe to display the expected slide number.
    /// Falls back gracefully if the label is not found.
    /// </summary>
    private static async Task WaitForSlideNumberAsync(IFrame frame, int expectedSlide, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var label = await frame.QuerySelectorAsync(SlideLabelSelector);
                if (label == null) return; // label not present — just proceed

                var text = await label.InnerTextAsync();
                // Label text is typically "Slide N of M"
                if (text.Contains(expectedSlide.ToString()))
                    return;
            }
            catch { /* frame may not be ready yet */ }

            await Task.Delay(300);
        }
        // Timeout is soft — proceed anyway and let the screenshot capture whatever is shown
    }

    private async Task EnsureBrowserAsync()
    {
        if (_browser != null) return;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-dev-shm-usage"],
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
