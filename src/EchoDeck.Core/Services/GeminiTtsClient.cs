using System.Net.Http.Json;
using System.Text.Json;

namespace EchoDeck.Core.Services;

public class GeminiTtsClient(HttpClient httpClient, FFmpegService ffmpegService) : ITtsClient
{
    private const string Model = "gemini-2.5-flash-preview-tts";

    public static readonly IReadOnlyList<string> KnownVoices =
    [
        "Zephyr", "Puck", "Charon", "Kore", "Fenrir", "Leda", "Orus", "Aoede",
        "Callirrhoe", "Autonoe", "Enceladus", "Iapetus", "Umbriel", "Algieba",
        "Despina", "Erinome", "Algenib", "Rasalgethi", "Laomedeia", "Achernar",
        "Alnilam", "Schedar", "Gacrux", "Pulcherrima", "Achird", "Zubenelgenubi",
        "Vindemiatrix", "Sadachbia", "Sadaltager", "Sulafat"
    ];

    public async Task<byte[]> TextToSpeechAsync(string voiceId, string text, CancellationToken ct = default)
    {
        var voiceName = voiceId.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase)
            ? voiceId[7..]
            : voiceId;

        var body = new
        {
            contents = new[] { new { parts = new[] { new { text } } } },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new
                {
                    voiceConfig = new
                    {
                        prebuiltVoiceConfig = new { voiceName }
                    }
                }
            }
        };

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"v1beta/models/{Model}:generateContent");
                request.Content = JsonContent.Create(body);

                using var response = await httpClient.SendAsync(request, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt <= 8)
                {
                    // Free-tier Gemini has strict RPM limits; back off aggressively
                    var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)) + Random.Shared.NextDouble() * 5);
                    await Task.Delay(delay, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var base64Pcm = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("inlineData")
                    .GetProperty("data")
                    .GetString()
                    ?? throw new InvalidOperationException("Gemini TTS returned no audio data.");

                var pcmBytes = Convert.FromBase64String(base64Pcm);
                return await ffmpegService.ConvertPcmToMp3Async(pcmBytes, ct);
            }
            catch (HttpRequestException) when (attempt <= 5)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble());
                await Task.Delay(delay, ct);
            }
        }
    }
}
