using System.Net.Http.Json;
using System.Text.Json;

namespace EchoDeck.Core.Services;

public class ElevenLabsClient(HttpClient httpClient) : ITtsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<byte[]> TextToSpeechAsync(
        string voiceId,
        string text,
        CancellationToken ct = default)
    {
        var body = new
        {
            text,
            model_id = "eleven_multilingual_v2",
            voice_settings = new { stability = 0.35, similarity_boost = 0.85, style_exaggeration = 0 }
        };

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"v1/text-to-speech/{voiceId}");
                request.Content = JsonContent.Create(body, options: JsonOptions);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("audio/mpeg"));

                using var response = await httpClient.SendAsync(request, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt <= 3)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble());
                    await Task.Delay(delay, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException) when (attempt <= 3)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble());
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<List<(string Id, string Name)>> GetVoicesAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync("v1/voices", ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var voices = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty("voices", out var voicesEl))
        {
            foreach (var v in voicesEl.EnumerateArray())
            {
                var id = v.GetProperty("voice_id").GetString() ?? string.Empty;
                var name = v.GetProperty("name").GetString() ?? string.Empty;
                voices.Add((id, name));
            }
        }
        return voices;
    }
}
