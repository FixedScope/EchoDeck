namespace EchoDeck.Core.Services;

public class TtsRouter(ElevenLabsClient? elevenLabsClient, GeminiTtsClient? geminiClient)
{
    public Task<byte[]> TextToSpeechAsync(string voiceId, string text, CancellationToken ct = default) =>
        voiceId.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase)
            ? geminiClient != null
                ? geminiClient.TextToSpeechAsync(voiceId, text, ct)
                : throw new InvalidOperationException("GEMINI_API_KEY is not configured.")
            : elevenLabsClient != null
                ? elevenLabsClient.TextToSpeechAsync(voiceId, text, ct)
                : throw new InvalidOperationException("ELEVENLABS_API_KEY is not configured.");
}
