using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MDH.InsightsService.Anthropic;

public class AnthropicClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicClient> _logger;
    private const string Model = "claude-sonnet-4-20250514";

    public AnthropicClient(HttpClient httpClient, ILogger<AnthropicClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> SendMessageAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        // 🔴 BREAKPOINT: prompt assembled, sending to Claude
        _logger.LogInformation("Sending request to Anthropic Claude ({Model})", Model);

        var requestBody = new
        {
            model = Model,
            max_tokens = 1024,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("https://api.anthropic.com/v1/messages", content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        _logger.LogInformation("Anthropic response received ({Length} chars)", text.Length);
        return text;
    }
}
