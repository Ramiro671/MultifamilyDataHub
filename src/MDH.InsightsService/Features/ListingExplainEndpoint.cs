using System.Text.Json;
using MDH.InsightsService.Anthropic;
using MDH.Shared.DTOs;

namespace MDH.InsightsService.Features;

public static class ListingExplainEndpoint
{
    public static async Task<IResult> HandleAsync(
        ListingInsightRequestDto request,
        AnthropicClient anthropic,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        logger.LogInformation("Listing explain request for {ListingId}", request.ListingId);

        var analyticsBaseUrl = config["AnalyticsApi:BaseUrl"] ?? "http://localhost:5030";
        var client = httpClientFactory.CreateClient("AnalyticsApi");

        ListingDto? listing = null;
        List<AnomalyDto> anomalies = new();

        try
        {
            var listingResponse = await client.GetAsync(
                $"{analyticsBaseUrl}/api/v1/listings/{request.ListingId}", ct);
            if (listingResponse.IsSuccessStatusCode)
            {
                var json = await listingResponse.Content.ReadAsStringAsync(ct);
                listing = JsonSerializer.Deserialize<ListingDto>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            if (listing != null)
            {
                var anomalyResponse = await client.GetAsync(
                    $"{analyticsBaseUrl}/api/v1/listings/anomalies?submarket={Uri.EscapeDataString(listing.Submarket)}&since={DateTime.UtcNow.AddDays(-7):yyyy-MM-ddTHH:mm:ssZ}", ct);
                if (anomalyResponse.IsSuccessStatusCode)
                {
                    var json = await anomalyResponse.Content.ReadAsStringAsync(ct);
                    var all = JsonSerializer.Deserialize<List<AnomalyDto>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    anomalies = all?.Where(a => a.ListingId == request.ListingId).ToList() ?? new();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch listing data from AnalyticsApi");
        }

        var rawStats = new Dictionary<string, object>
        {
            ["listingId"] = request.ListingId,
            ["found"] = listing != null,
            ["anomalyCount"] = anomalies.Count
        };

        if (listing == null)
            return Results.NotFound(new { error = "Listing not found or AnalyticsApi unavailable" });

        var listingDataJson = JsonSerializer.Serialize(new { listing, anomalies });
        var systemPrompt = LoadPromptTemplate("listing-explain");
        var userMessage = $"Please explain why this listing stands out:\n\n{listingDataJson}";

        string claudeResponse;
        try
        {
            claudeResponse = await anthropic.SendMessageAsync(systemPrompt, userMessage, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get explanation from Claude");
            claudeResponse = JsonSerializer.Serialize(new
            {
                explanation = $"Listing {request.ListingId} in {listing.Submarket} has {anomalies.Count} anomaly flags.",
                standoutReason = anomalies.Count > 0 ? anomalies[0].FlagReason : "No anomalies detected.",
                riskLevel = "low"
            });
        }

        return Results.Ok(new InsightResponseDto(
            claudeResponse,
            anomalies.Count > 0 ? anomalies[0].FlagReason : null,
            rawStats,
            DateTime.UtcNow));
    }

    private static string LoadPromptTemplate(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", $"{name}.md");
        return File.Exists(path) ? File.ReadAllText(path) : $"You are a real estate analyst. Explain the following {name} data.";
    }
}
