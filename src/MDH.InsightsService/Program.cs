using System.Text;
using MDH.InsightsService.Anthropic;
using MDH.InsightsService.Features;
using MDH.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Polly;
using Polly.Extensions.Http;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .WriteTo.File("logs/insights-.log", rollingInterval: RollingInterval.Day));

    // JWT Auth (same secret as AnalyticsApi)
    var jwtSecret = builder.Configuration["JWT_SECRET"]
        ?? builder.Configuration["Jwt:Secret"]
        ?? "replace-with-64-char-random-replace-with-64-char-random-xxxxx";
    var key = Encoding.UTF8.GetBytes(jwtSecret);
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = false,
                ValidateAudience = false
            };
        });
    builder.Services.AddAuthorization();

    // Anthropic HttpClient with Polly retry + circuit breaker
    // Reads from "Anthropic:ApiKey" (works with Container Apps secrets as "Anthropic__ApiKey"
    // env var) or falls back to the legacy "ANTHROPIC_API_KEY" env var for local .env compat.
    var apiKey = builder.Configuration["Anthropic:ApiKey"]
        ?? builder.Configuration["ANTHROPIC_API_KEY"]
        ?? "";
    builder.Services.AddHttpClient<AnthropicClient>(client =>
    {
        client.BaseAddress = new Uri("https://api.anthropic.com");
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    })
    .AddPolicyHandler(GetRetryPolicy())
    .AddPolicyHandler(GetCircuitBreakerPolicy());

    // AnalyticsApi HttpClient (for fetching metrics)
    var analyticsJwt = builder.Configuration["AnalyticsApi:DemoToken"] ?? "";
    builder.Services.AddHttpClient("AnalyticsApi", client =>
    {
        if (!string.IsNullOrEmpty(analyticsJwt))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", analyticsJwt);
    })
    .AddPolicyHandler(GetRetryPolicy());

    // Swagger
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "MDH Insights Service", Version = "v1" });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header
        });
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
                Array.Empty<string>()
            }
        });
    });

    // Readiness: verify the upstream AnalyticsApi is reachable.
    // We do NOT call Anthropic on every probe to avoid burning tokens.
    var analyticsBaseUrl = builder.Configuration["AnalyticsApi:BaseUrl"] ?? "http://localhost:5030";
    builder.Services.AddHealthChecks()
        .AddUrlGroup(new Uri($"{analyticsBaseUrl}/health"), name: "analytics-api", tags: ["ready"]);

    builder.WebHost.UseUrls("http://+:5040");

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "MDH Insights Service v1"));
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false  // Liveness: always 200
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    var api = app.MapGroup("/api/v1").RequireAuthorization();

    api.MapPost("/insights/market-summary", (
        InsightRequestDto request,
        AnthropicClient anthropic,
        IHttpClientFactory factory,
        IConfiguration config,
        ILogger<InsightEndpoints> logger,
        CancellationToken ct) =>
        MarketSummaryEndpoint.HandleAsync(request, anthropic, factory, config, logger, ct))
    .WithName("GetMarketSummary")
    .WithSummary("AI-generated market summary using Claude");

    api.MapPost("/insights/listing-explain", (
        ListingInsightRequestDto request,
        AnthropicClient anthropic,
        IHttpClientFactory factory,
        IConfiguration config,
        ILogger<InsightEndpoints> logger,
        CancellationToken ct) =>
        ListingExplainEndpoint.HandleAsync(request, anthropic, factory, config, logger, ct))
    .WithName("ExplainListing")
    .WithSummary("AI explanation of why a listing stands out");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "InsightsService terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt =>
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy() =>
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
