namespace EchoDeck.Core.Services;

public interface ITtsClient
{
    Task<byte[]> TextToSpeechAsync(string voiceId, string text, CancellationToken ct = default);
}
