# 09 — MDH.AnalyticsApi — REST API with CQRS

> **Study time:** ~35 minutes
> **Prerequisites:** [`03_DATA_WAREHOUSING_SQLSERVER.md`](./03_DATA_WAREHOUSING_SQLSERVER.md), [`11_REST_API_DESIGN_CSHARP.md`](./11_REST_API_DESIGN_CSHARP.md)

## Why this matters

REST API design is the number one skill listed in the Smart Apartment Data job description. This service demonstrates every relevant pattern: Minimal API vs controllers, CQRS with MediatR, JWT auth, Swagger, health checks, and EF Core read-optimized queries. If you can walk a senior engineer through `GetMarketsQueryHandler.cs` and explain every line, you have shown that you understand how modern .NET APIs are built.

By the end of this doc you will be able to: (1) explain the CQRS pattern and trace a request from HTTP verb to MediatR handler to EF Core to JSON response; (2) articulate when to use `AsNoTracking()` and why it is not optional for read paths; (3) describe the JWT auth setup and explain the difference between `ValidateIssuer: false` and a fully configured issuer.

---

## Minimal API vs Controllers

ASP.NET Core 6+ introduced Minimal APIs — a way to define endpoints without a controller class:

```csharp
// Minimal API (what we use)
app.MapGet("/api/v1/markets", async (IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(new GetMarketsQuery(), ct)));

// Controller equivalent
[ApiController]
[Route("api/v1/markets")]
public class MarketsController : ControllerBase {
    [HttpGet]
    public async Task<IActionResult> Get(IMediator mediator, ...) { ... }
}
```

**When Minimal APIs win:** Lower ceremony, easier to reason about for small APIs, slightly better performance (less middleware overhead), natural fit for functional/handler patterns.

**When Controllers win:** Large APIs with many endpoints benefit from controller grouping; attribute-based routing is more familiar to teams with ASP.NET MVC history; filter attributes (`[Authorize]`, `[ValidateAntiForgeryToken]`) are easier to apply at class level.

We use Minimal APIs. The `.RequireAuthorization()` call on the route group (`var api = app.MapGroup("/api/v1").RequireAuthorization()` in `Program.cs` line 101) applies JWT auth to all routes in the group without repeating the attribute on every handler.

---

## CQRS with MediatR

**CQRS (Command Query Responsibility Segregation):** Separate the read model (queries) from the write model (commands). Read operations (`GetMarketsQuery`) return DTOs optimized for the query shape. Write operations (`CreateListingCommand`, not in this project yet) mutate state. They use different code paths and can use different data models.

**Why CQRS here:** AnalyticsApi is read-only today. All six endpoints are queries. The value of MediatR is: (1) each handler is a single-responsibility class, trivially testable; (2) pipeline behaviors (logging, caching, validation) can be inserted without touching handler code; (3) `IRequest<T>` makes the contract explicit — the query type IS the input contract.

**MediatR registration** (`Program.cs` line 30):
```csharp
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
```
This scans the `MDH.AnalyticsApi` assembly for all `IRequestHandler<TRequest, TResponse>` implementations and registers them with DI. No manual registration per handler.

---

## Handler Deep Dive — `GetMarketsQueryHandler`

Full file: `src/MDH.AnalyticsApi/Features/Markets/GetMarketsQuery.cs`

```csharp
public class GetMarketsQueryHandler : IRequestHandler<GetMarketsQuery, IReadOnlyList<MarketSummaryDto>>
{
    private readonly AnalyticsDbContext _db;
    public GetMarketsQueryHandler(AnalyticsDbContext db) => _db = db;

    public async Task<IReadOnlyList<MarketSummaryDto>> Handle(
        GetMarketsQuery request, CancellationToken ct)
    {
        // 🔴 BREAKPOINT: query received
        var submarkets = await _db.DimSubmarkets.AsNoTracking().ToListAsync(ct);

        var latestDate = await _db.FactMarketMetrics
            .AsNoTracking()
            .MaxAsync(m => (DateOnly?)m.MetricDate, ct);

        var latestMetrics = latestDate.HasValue
            ? await _db.FactMarketMetrics.AsNoTracking()
                .Include(m => m.Submarket)
                .Where(m => m.MetricDate == latestDate.Value)
                .ToListAsync(ct)
            : new List<WarehouseMarketMetrics>();

        return submarkets.Select(s => new MarketSummaryDto(
            s.SubmarketId, s.Name, s.State, s.Region,
            latestMetrics.Where(m => m.SubmarketId == s.SubmarketId)
                .Select(m => new MarketMetricsDto(...))
                .ToList().AsReadOnly()
        )).ToList().AsReadOnly();
    }
}
```

Three observations:
1. Every EF Core query calls `AsNoTracking()` — discussed below.
2. The handler runs two queries: one to get all submarkets (12 rows, fast), one to get the latest metrics. This N+1-avoidance is explicit — one parameterized query per logical concern.
3. The final shaping (`.Select(s => new MarketSummaryDto(...))`) happens in .NET after the data is loaded — appropriate since the final join (metrics by submarket) is a 12-row in-memory operation.

---

## EF Core Read Patterns

**`AsNoTracking()`:** Disables the EF Core change tracker for this query. Without it, every entity loaded registers a snapshot in the context's identity map. For 10,000 listings, this allocates 10,000 `EntityEntry` objects that are immediately discarded because you never call `SaveChanges()`. Benchmark: `AsNoTracking` is ~2x faster than tracked reads for large result sets.

**Projection with `.Select()`:** Instead of loading full entities and then mapping, project directly to DTO in the query:

```csharp
// Better for large result sets — SQL returns only needed columns
var dtos = await _db.DimListings
    .AsNoTracking()
    .Where(l => l.IsActive)
    .Select(l => new ListingDto(l.ListingId, l.ExternalId, ...))
    .ToListAsync(ct);
```

When you `.Select()` before `.ToListAsync()`, EF Core generates `SELECT ListingId, ExternalId, ...` — only the columns you need. Without projection, it generates `SELECT *` and materializes the full entity. At 50 columns per row × 100,000 rows, this is a significant difference.

**`.Include()` for navigation properties:** `Include(m => m.Submarket)` adds a `LEFT JOIN` to the SQL query, loading the related entity in the same round trip. Use it for small, frequently needed navigations (submarket name). Avoid it for unbounded collections (all daily rents for a listing = potentially thousands of rows per listing).

---

## Endpoint Table

| Method | Route | Auth | Handler file | Status codes |
|---|---|---|---|---|
| GET | `/api/v1/markets` | Bearer | `Features/Markets/GetMarketsQuery.cs` | 200 |
| GET | `/api/v1/markets/{submarket}/metrics` | Bearer | `Features/Markets/GetMarketMetricsQuery.cs` | 200 |
| GET | `/api/v1/markets/{submarket}/comps` | Bearer | `Features/Markets/GetCompsQuery.cs` | 200 |
| GET | `/api/v1/listings/search` | Bearer | `Features/Listings/SearchListingsQuery.cs` | 200 |
| GET | `/api/v1/listings/anomalies` | Bearer | `Features/Listings/GetAnomaliesQuery.cs` | 200 |
| GET | `/api/v1/listings/{id:guid}` | Bearer | `Features/Listings/GetListingByIdQuery.cs` | 200, 404 |

All routes are under `app.MapGroup("/api/v1").RequireAuthorization()` in `Program.cs` line 101 — JWT Bearer auth is applied once for all routes.

---

## JWT Bearer Authentication

`Program.cs` lines 33–47:

```csharp
var jwtSecret = builder.Configuration["JWT_SECRET"]
    ?? builder.Configuration["Jwt:Secret"]
    ?? "replace-with-64-char-random-replace-with-64-char-random-xxxxx";
var key = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts => {
        opts.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = false,
            ValidateAudience = false
        };
    });
```

**HS256** (HMAC-SHA256) with a symmetric secret: the same key signs and verifies tokens. This is appropriate for a system with a single trusted issuer (you). For multi-issuer systems (SSO, OpenID Connect), use RS256 with asymmetric keys (private key signs, public key verifies — the public key can be distributed).

**`ValidateIssuer: false`** — tokens are accepted regardless of the `iss` claim value. Fine for demos and internal APIs. In production, set `ValidIssuer` to your token issuer URL to prevent token reuse from other systems.

**JWT secret fallback chain:** The code checks `JWT_SECRET` env var first, then `Jwt:Secret` in config, then falls back to the demo string. The container override via `SQL_CONNECTION_STRING` pattern (same bug was present here — fixed by putting the env var first in the `??` chain) is documented in `AUDIT_REPORT.md`. If `appsettings.json` has a non-null value for `Jwt:Secret`, the `??` fallback to the env var is never reached — the env var is masked.

---

## Swagger / OpenAPI

`Program.cs` lines 51–70 add Swagger with a Bearer security scheme. The result:
- `GET /swagger` opens the Swagger UI
- The "Authorize" button lets you paste a JWT and it is sent as `Authorization: Bearer <token>` on every Try-It-Out request
- The spec is auto-generated from the endpoint definitions and `.Produces<T>()` annotations

**Code-first OpenAPI** (our approach): Generate the spec from the C# code. Advantage: spec is always in sync with implementation. Disadvantage: less control over the spec shape.

**Contract-first OpenAPI**: Write the spec in YAML first, generate server stubs. Advantage: spec is the source of truth, clients can be generated before the server is built. Used in API platforms where frontend and backend teams agree on the contract before either writes code.

---

## Health Checks

`Program.cs` lines 74–75, 90–98:

```csharp
builder.Services.AddHealthChecks()
    .AddSqlServer(sqlConn, name: "sql-server", tags: ["ready"]);

app.MapHealthChecks("/health", new HealthCheckOptions {
    Predicate = _ => false  // Liveness: always 200
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready")  // Readiness: SQL must respond
});
```

Liveness (`/health`) always returns 200 if the process is alive. It does not check SQL because Azure SQL free tier auto-pauses after 60 minutes — a SQL probe would return 503 after every idle period, causing Container Apps to kill the container. Readiness (`/health/ready`) checks SQL and is used only by deployment checks, not the liveness probe.

See the full explanation in [`14_CLOUD_FUNDAMENTALS.md`](./14_CLOUD_FUNDAMENTALS.md) and the post-mortem in `AUDIT_REPORT.md`.

---

## Pagination — Offset vs Keyset

`SearchListingsQuery.cs` uses offset pagination:

```csharp
.Skip((request.Page - 1) * request.PageSize)
.Take(request.PageSize)
```

**Offset pagination pros:** Arbitrary page jumps (`?page=50`), simple to implement.

**Offset pagination cons:** At large offsets (page 1000 with pageSize 25 = skip 24,975 rows), SQL Server must scan and discard all preceding rows even if they are never returned. Performance degrades linearly with page number.

**Keyset/cursor pagination:** Instead of `SKIP`, use `WHERE id > @lastSeenId ORDER BY id`. SQL Server can seek directly to the right row via the index. Performance is O(pageSize) regardless of position. Limitation: you cannot jump to an arbitrary page number.

For this API (paginated listing search), offset is appropriate because users scroll through a few pages at most. For infinite scroll feeds with millions of items, keyset is mandatory.

---

## Exercise

1. Open `SearchListingsQuery.cs` lines 35–57. The handler loads `DimListings` with `.Include(l => l.DailyRents)`. For a listing with 365 days of rent history, how many rows does this load? What refactoring would reduce this to 1 rent row per listing?

2. Open `Program.cs` line 101: `var api = app.MapGroup("/api/v1").RequireAuthorization()`. What happens if a request arrives without an `Authorization` header? Trace the middleware pipeline that produces the 401 response.

3. Write the MediatR pipeline behavior skeleton for a caching layer that caches `GetMarketsQuery` results for 5 minutes using `IMemoryCache`.

4. The `GetMarketsQueryHandler` runs two separate SQL queries (submarkets, then metrics). In high-traffic scenarios, could a race condition produce inconsistent results? How would you prevent it?

---

## Common mistakes

- **Missing `AsNoTracking()` on read paths.** This is not optional. Every EF context scoped to a request should use `AsNoTracking` for read-only queries. Not doing so slows down large queries and adds memory pressure.

- **Loading entire entities when you only need a few columns.** Without `.Select()` projection, EF Core generates `SELECT *`. At 50 columns × 100k rows, this is a significant waste. Project to DTOs in the query.

- **Putting business logic in endpoints.** The endpoint (`app.MapGet(...)`) should only dispatch to MediatR and return. Filtering, sorting, mapping — all in the handler. Endpoints that grow fat are hard to test.

- **JWT secret hardcoded in appsettings.json.** The fallback demo string `"replace-with-64-char-random..."` is for local development only. In Azure, the `JWT_SECRET` env var (set as a Container Apps secret) must be checked FIRST in the `??` chain, before `GetValue("Jwt:Secret")`. Otherwise, `appsettings.json` shadows the secret.

- **`ValidateIssuer: false` in production.** Disabling issuer validation means a token signed by any issuer with the same secret is accepted. For demos this is fine. For production, always set `ValidIssuer` and `ValidAudience`.

---

## Interview Angle — Smart Apartment Data

1. **"What is CQRS and where do you use it?"** — Command Query Responsibility Segregation separates reads and writes into separate handlers with separate request/response types. We use MediatR to dispatch. Every `GET` endpoint has a `Query` class that `IRequestHandler` handles. Benefits: each handler is single-responsibility, testable in isolation, and pipeline behaviors (logging, caching, validation) can be added without touching handler logic.

2. **"Explain AsNoTracking and why it matters."** — EF Core's change tracker maintains a snapshot of every entity for dirty detection. On read-only queries, those snapshots are pure overhead. `AsNoTracking()` skips tracking, reducing memory allocation by ~50% on large result sets and speeding up materialization. It is not optional for read paths at production scale.

3. **"How does JWT auth work in this API?"** — The server validates the token's HMAC signature using the shared secret. If the signature is valid, the request is authenticated. No session state, no database lookup — stateless validation. The Bearer token is passed in the `Authorization` header. All routes under `/api/v1` require a valid token via `RequireAuthorization()` on the route group.

4. **"What would you add to make the API production-ready?"** — Response caching or output caching for `/markets` (changes every 5 minutes); rate limiting middleware; ProblemDetails error responses (RFC 7807); distributed tracing via OpenTelemetry; role-based authorization (read-only vs admin roles).

5. **30-second talking point:** "AnalyticsApi is a read-only REST API built with ASP.NET Core 9 Minimal APIs and MediatR. Every endpoint dispatches to a CQRS query handler that uses EF Core with AsNoTracking for efficient warehouse reads. JWT Bearer auth is applied at the route group level. Health checks separate liveness from readiness to handle Azure SQL's auto-pause behavior. The Swagger UI at /swagger lets you authorize with a Bearer token and try all endpoints interactively."

6. **Job requirement proof:** "REST API design with C#" — 6 endpoints, Minimal API, MediatR CQRS, JWT Bearer, Swagger/OpenAPI, health check separation, EF Core read patterns, pagination.
