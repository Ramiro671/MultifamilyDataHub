using System.Text.Json;
using MDH.InsightsService.Anthropic;
using MDH.Shared.DTOs;

namespace MDH.InsightsService.Features;

public static class MarketSummaryEndpoint
{
    public static async Task<IResult> HandleAsync(
        InsightRequestDto request,
        AnthropicClient anthropic,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<InsightEndpoints> logger,
        CancellationToken ct)
    {
        logger.LogInformation("Market summary request for {Submarket} from {From} to {To}",
            request.Submarket, request.FromDate, request.ToDate);

        // Fetch market metrics from AnalyticsApi
        var analyticsBaseUrl = config["AnalyticsApi:BaseUrl"] ?? "http://localhost:5030";
        var client = httpClientFactory.CreateClient("AnalyticsApi");

        List<MarketMetricsDto> metrics;
        try
        {
            var fromStr = request.FromDate.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var toStr = request.ToDate.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var url = $"{analyticsBaseUrl}/api/v1/markets/{Uri.EscapeDataString(request.Submarket)}/metrics?from={fromStr}&to={toStr}";
            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            metrics = JsonSerializer.Deserialize<List<MarketMetricsDto>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch metrics from AnalyticsApi, using empty data");
            metrics = new List<MarketMetricsDto>();
        }

        // Build raw stats
        var rawStats = new Dictionary<string, object>
        {
            ["submarket"] = request.Submarket,
            ["fromDate"] = request.FromDate,
            ["toDate"] = request.ToDate,
            ["metricsCount"] = metrics.Count,
            ["avgRent"] = metrics.Count > 0 ? metrics.Average(m => (double)m.AvgRent) : 0,
            ["avgOccupancy"] = metrics.Count > 0 ? metrics.Average(m => (double)m.OccupancyEstimate) : 0
        };

        // Build prompt
        var marketDataJson = JsonSerializer.Serialize(new { submarket = request.Submarket, metrics });
        var systemPrompt = LoadPromptTemplate("market-summary");
        var userMessage = $"Please analyze the following market data and provide a summary:\n\n{marketDataJson}";

        string claudeResponse;
        try
        {
            claudeResponse = await anthropic.SendMessageAsync(systemPrompt, userMessage, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get response from Claude");
            claudeResponse = JsonSerializer.Serialize(new
            {
                summary = $"Market data analysis for {request.Submarket} is temporarily unavailable. The market shows {metrics.Count} data points.",
                keyStats = rawStats,
                sentiment = "neutral"
            });
        }

        return Results.Ok(new InsightResponseDto(
            claudeResponse,
            null,
            rawStats,
            DateTime.UtcNow));
    }

    private static string LoadPromptTemplate(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", $"{name}.md");
        return File.Exists(path) ? File.ReadAllText(path) : $"You are a real estate market analyst. Analyze the provided {name} data.";
    }
}
