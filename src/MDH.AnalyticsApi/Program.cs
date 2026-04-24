using System.Text;
using MediatR;
using MDH.AnalyticsApi.Features.Listings;
using MDH.AnalyticsApi.Features.Markets;
using MDH.AnalyticsApi.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .WriteTo.File("logs/analytics-.log", rollingInterval: RollingInterval.Day));

    // EF Core
    var sqlConn = builder.Configuration.GetConnectionString("SqlServer")
        ?? builder.Configuration["SQL_CONNECTION_STRING"]
        ?? throw new InvalidOperationException("SQL connection string not configured");
    builder.Services.AddDbContext<AnalyticsDbContext>(opts => opts.UseSqlServer(sqlConn));

    // MediatR
    builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

    // JWT Auth
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

    // Swagger
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "MDH Analytics API", Version = "v1" });
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

    // Health checks
    builder.Services.AddHealthChecks()
        .AddSqlServer(sqlConn, name: "sql-server");

    builder.WebHost.UseUrls("http://+:5030");

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "MDH Analytics API v1"));

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/ready");

    // API routes — all require auth
    var api = app.MapGroup("/api/v1").RequireAuthorization();

    // Markets
    api.MapGet("/markets", async (IMediator mediator, CancellationToken ct) =>
    {
        var result = await mediator.Send(new GetMarketsQuery(), ct);
        return Results.Ok(result);
    })
    .WithName("GetMarkets")
    .WithSummary("List submarkets with latest metrics")
    .Produces<IReadOnlyList<MarketSummaryDto>>();

    api.MapGet("/markets/{submarket}/metrics", async (
        string submarket,
        DateTime? from,
        DateTime? to,
        IMediator mediator,
        CancellationToken ct) =>
    {
        var result = await mediator.Send(new GetMarketMetricsQuery(
            submarket,
            from ?? DateTime.UtcNow.AddDays(-30),
            to ?? DateTime.UtcNow), ct);
        return Results.Ok(result);
    })
    .WithName("GetMarketMetrics")
    .WithSummary("Historical market metrics for a submarket");

    api.MapGet("/markets/{submarket}/comps", async (
        string submarket,
        int? bedrooms,
        int? sqftMin,
        int? sqftMax,
        IMediator mediator,
        CancellationToken ct) =>
    {
        var result = await mediator.Send(new GetCompsQuery(
            submarket, bedrooms ?? 2, sqftMin ?? 0, sqftMax ?? 999999), ct);
        return Results.Ok(result);
    })
    .WithName("GetComps")
    .WithSummary("Comparable listings in a submarket");

    // Listings
    api.MapGet("/listings/search", async (
        string? submarket,
        int? bedrooms,
        decimal? minRent,
        decimal? maxRent,
        int? page,
        int? pageSize,
        IMediator mediator,
        CancellationToken ct) =>
    {
        var result = await mediator.Send(new SearchListingsQuery(
            submarket, bedrooms, minRent, maxRent,
            page ?? 1, pageSize ?? 25), ct);
        return Results.Ok(result);
    })
    .WithName("SearchListings")
    .WithSummary("Paginated listing search");

    api.MapGet("/listings/anomalies", async (
        string? submarket,
        DateTime? since,
        IMediator mediator,
        CancellationToken ct) =>
    {
        var result = await mediator.Send(
            new GetAnomaliesQuery(submarket, since ?? DateTime.UtcNow.AddDays(-7)), ct);
        return Results.Ok(result);
    })
    .WithName("GetAnomalies")
    .WithSummary("Anomalous listings flagged by 3-sigma rule");

    api.MapGet("/listings/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
    {
        var result = await mediator.Send(new GetListingByIdQuery(id), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    })
    .WithName("GetListingById")
    .WithSummary("Single listing detail");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AnalyticsApi terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
