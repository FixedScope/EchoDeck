using EchoDeck.Core.Pipeline;
using Microsoft.Playwright;

namespace EchoDeck.Core.Tests;

/// <summary>
/// Validates Office Online rendering via a cloudflared tunnel.
/// Set BASE_URL to your tunnel URL before running.
/// Screenshots are saved to oo-screenshot/ in the repo root for inspection.
/// </summary>
public class OfficeOnlineRendererTests
{
    [Fact]
    public async Task OfficeOnline_ViaTunnel_RendersAllSlides()
    {
        var baseUrl = Environment.GetEnvironmentVariable("BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("railway.app"))
        {
            Console.WriteLine("Skipping — set BASE_URL to a cloudflared tunnel URL.");
            return;
        }

        var assemblyDir = Path.GetDirectoryName(typeof(OfficeOnlineRendererTests).Assembly.Location)!;
        var fixturePath = Path.Combine(assemblyDir, "fixtures", "sample.pptx");
        if (!File.Exists(fixturePath))
        {
            Console.WriteLine("Skipping — fixture not found.");
            return;
        }

        // Place the pptx where the server's static file middleware can serve it.
        // Server (EchoDeck.Mcp) serves DATA_DIR=./data at /jobs-data/.
        // When running locally, ./data is relative to src/EchoDeck.Mcp/.
        var serverDataDir = Path.GetFullPath(Path.Combine(
            assemblyDir, "..", "..", "..", "..",
            "src", "EchoDeck.Mcp", "data", "oo-test"));
        Directory.CreateDirectory(serverDataDir);

        var pptxDest = Path.Combine(serverDataDir, "sample.pptx");
        File.Copy(fixturePath, pptxDest, overwrite: true);

        // Verify reachable through tunnel
        var pptxPublicUrl = $"{baseUrl.TrimEnd('/')}/jobs-data/oo-test/sample.pptx";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, pptxPublicUrl));
        Assert.True(head.IsSuccessStatusCode, $"pptx not reachable at {pptxPublicUrl}");
        Console.WriteLine($"pptx reachable: {pptxPublicUrl}");

        // Output dir (persistent so screenshots can be inspected)
        var repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
        var outDir = Path.Combine(repoRoot, "oo-screenshot");
        Directory.CreateDirectory(outDir);

        // Use the renderer directly, pointing at the tunnel
        await using var renderer = new OfficeOnlineRenderer(baseUrl);

        // The renderer derives the pptx URL from jobId (parent dir of pptxPath) + filename.
        // We fake a "job" by placing the pptx at <serverDataDir>/sample.pptx and
        // constructing a path that resolves to the right URL segment.
        // Since the renderer reads jobId from Path.GetDirectoryName, we need the parent
        // dir to be "oo-test" — which it already is (serverDataDir ends with "oo-test").
        // But our server serves it under /jobs-data/ not /temp/, so we use a direct
        // Playwright approach for this test and call the renderer's logic manually.

        // Instead: use Playwright directly with the corrected selectors to verify rendering.
        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-dev-shm-usage"],
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
        });
        var page = await context.NewPageAsync();

        var embedUrl = $"https://view.officeapps.live.com/op/embed.aspx?src={Uri.EscapeDataString(pptxPublicUrl)}";
        Console.WriteLine($"Opening: {embedUrl}");

        await page.GotoAsync(embedUrl, new PageGotoOptions { Timeout = 60_000 });
        await page.WaitForSelectorAsync("#wacframe", new PageWaitForSelectorOptions { Timeout = 30_000 });

        // Wait for wacframe to load its content
        IFrame? wacFrame = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            wacFrame = page.Frames.FirstOrDefault(f => f.Name == "wacframe" && f.Url.Contains("officeapps"));
            if (wacFrame != null) break;
            await Task.Delay(500);
        }
        Assert.NotNull(wacFrame);
        Console.WriteLine($"wacframe URL: {wacFrame.Url[..Math.Min(80, wacFrame.Url.Length)]}");

        // Wait for slide canvas inside the iframe
        await wacFrame.WaitForSelectorAsync("#SlidePanel", new FrameWaitForSelectorOptions { Timeout = 30_000 });
        Console.WriteLine("SlidePanel found — slides are rendering.");

        // Screenshot all 6 slides
        var slideCount = 6;
        for (int i = 0; i < slideCount; i++)
        {
            await Task.Delay(1000); // let canvas render

            var canvas = await wacFrame.QuerySelectorAsync("canvas");
            var screenshotPath = Path.Combine(outDir, $"slide_{i + 1:D2}.png");

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
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
            }

            var fi = new FileInfo(screenshotPath);
            Console.WriteLine($"  Slide {i + 1}: {fi.Length / 1024} KB → {screenshotPath}");
            Assert.True(fi.Length > 10_000, $"Slide {i + 1} screenshot too small — may be blank ({fi.Length} bytes)");

            // Advance to next slide
            if (i < slideCount - 1)
            {
                var nextBtn = await wacFrame.QuerySelectorAsync("#ButtonFastFwd-Small14");
                if (nextBtn != null)
                    await nextBtn.ClickAsync();
                else
                    await page.Keyboard.PressAsync("ArrowRight");
            }
        }

        await context.CloseAsync();
        await browser.DisposeAsync();

        Console.WriteLine($"\nAll slides saved to: {outDir}");
        Console.WriteLine("Open them in Explorer to verify visual quality.");
    }
}
