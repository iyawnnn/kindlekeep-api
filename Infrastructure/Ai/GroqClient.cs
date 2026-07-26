using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KindleKeep.Api.Infrastructure.Ai;

public record GroqMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content
);
public record GroqRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<GroqMessage> Messages
);

public record GroqChoice([property: JsonPropertyName("message")] GroqMessage? Message);
public record GroqResponse([property: JsonPropertyName("choices")] List<GroqChoice>? Choices);

// Thin wrapper around Groq's OpenAI-compatible chat completions endpoint - mirrors the
// "GroqClient" named HttpClient registered in Program.cs (same pattern as ResendClient/DiscordClient).
// Chosen over Gemini for its much higher free-tier rate limits.
public class GroqClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<GroqClient> logger)
{
    private readonly string _apiKey = configuration["Ai:GroqApiKey"]
        ?? Environment.GetEnvironmentVariable("KK_GROQ_API_KEY")
        ?? throw new InvalidOperationException("Groq API key is missing.");

    private readonly string _model = configuration["Ai:GroqModel"] ?? "llama-3.3-70b-versatile";

    public async Task<string> GenerateExplanationAsync(string prompt, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("GroqClient");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var request = new GroqRequest(_model, [new GroqMessage("user", prompt)]);
        var body = JsonSerializer.Serialize(request, AppJsonSerializerContext.Default.GroqRequest);

        const string url = "https://api.groq.com/openai/v1/chat/completions";
        using var response = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Groq request failed with {StatusCode}: {Body}", response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonSerializer.Deserialize(responseJson, AppJsonSerializerContext.Default.GroqResponse);

        var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogWarning("Groq returned no usable explanation text. Raw response: {Body}", responseJson);
            throw new InvalidOperationException("Groq returned no explanation text.");
        }

        return text.Trim();
    }
}
