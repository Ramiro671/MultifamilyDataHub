# 11 — REST API Design in C#

> **Study time:** ~30 minutes
> **Prerequisites:** none (standalone reference)

## Why this matters

REST API design is the first thing a backend engineer is evaluated on at a company that sells API-driven data products. Smart Apartment Data serves rental market analytics to clients via REST. "REST API design with C#" is the first job requirement on the JD. You need to be able to discuss URI design, HTTP semantics, status codes, versioning, pagination, and security fluently — not just as concepts but as code choices you can justify.

By the end of this doc you will be able to: (1) classify any API by Richardson Maturity Model level and explain what it would take to move it to the next level; (2) choose the correct HTTP status code for any outcome in an ASP.NET Core endpoint; (3) defend every URI and versioning choice in `MDH.AnalyticsApi` with a concrete rationale.

---

## Richardson Maturity Model

The RMM is a way to classify how "RESTful" an API is. Most APIs live at Level 2.

**Level 0 — The Swamp of POX (Plain Old XML/JSON):** One endpoint for everything. All calls are POST. Payload contains the action and parameters. SOAP APIs are here. Example: `POST /api` with body `{ action: "getMarkets" }`.

**Level 1 — Resources:** Multiple URIs, each representing a resource. But still uses only GET and POST regardless of operation semantics. Example: `POST /markets/delete/1` instead of `DELETE /markets/1`.

**Level 2 — HTTP Verbs:** Resources (Level 1) + semantically correct HTTP methods. This is where MDH.AnalyticsApi lives. `GET /api/v1/markets` retrieves. `GET /api/v1/listings/{id}` retrieves one. HTTP status codes carry meaning (200, 201, 404, etc.).

**Level 3 — Hypermedia (HATEOAS):** Responses include links to related actions. Example: a listing response includes `{ "_links": { "anomalies": "/api/v1/listings/123/anomalies" } }`. Clients navigate the API by following links rather than hardcoding URIs. Almost no production APIs implement this because the client complexity is rarely worth the benefit. MDH.AnalyticsApi does not implement it — deliberately.

---

## URI Design

**Nouns, not verbs:** URIs identify resources, not actions. The HTTP verb is the action.

| Wrong | Correct |
|---|---|
| `GET /getMarkets` | `GET /api/v1/markets` |
| `POST /createListing` | `POST /api/v1/listings` |
| `GET /fetchAnomaliesForAustin` | `GET /api/v1/listings/anomalies?submarket=Austin` |

**Collections vs individual resources:**
- Collection: `GET /api/v1/listings` — returns an array
- Single resource: `GET /api/v1/listings/{id}` — returns one item
- Nested resource: `GET /api/v1/markets/{submarket}/metrics` — metrics belonging to a submarket

**Plural vs singular:** Use plural for collections (`/listings`, `/markets`). The route `/listings/{id}` returns one listing — plural name, singular result. This is the widely accepted convention.

**Query parameters for filtering/sorting/pagination:**
```
GET /api/v1/listings/search?submarket=Austin&bedrooms=2&minRent=1500&maxRent=2500&page=1&pageSize=25
```
Filters and pagination are query parameters, not path segments. Path segments identify the resource; query parameters refine the result set.

**URI stability:** Once published, URIs should not change. Breaking URI changes require a new API version. This is why versioning matters.

---

## HTTP Verbs — Semantics, Safety, Idempotency

| Verb | Semantics | Safe? | Idempotent? | Notes |
|---|---|---|---|---|
| GET | Retrieve | Yes | Yes | No side effects |
| POST | Create / trigger action | No | No | Each call may create a new resource |
| PUT | Replace entire resource | No | Yes | PUT twice = same result |
| PATCH | Partial update | No | No (depends on impl) | Only send changed fields |
| DELETE | Remove | No | Yes | DELETE twice = resource gone (same result) |
| HEAD | GET metadata only | Yes | Yes | Returns headers, no body |
| OPTIONS | Describe allowed methods | Yes | Yes | Used for CORS preflight |

**Safe:** No side effects — calling it does not change server state.
**Idempotent:** Calling it multiple times produces the same result as calling it once.

**GET vs POST for searches:** MDH.AnalyticsApi uses `GET /listings/search?...` for paginated search. Some teams use `POST /listings/search` with a JSON body to avoid URL length limits. GET is preferred for read operations because: results can be cached (shared cache key = URL); GET is bookmarkable; it is semantically correct (read, no side effect).

**PUT vs PATCH:** PUT replaces the entire resource — if you send `{ name: "Austin" }` to a PUT endpoint, all other fields are cleared. PATCH updates only the sent fields. For partial updates, PATCH is correct but harder to implement correctly with JSON Merge Patch (RFC 7386) or JSON Patch (RFC 6902).

---

## HTTP Status Codes — The Full Table

**2xx — Success:**
- `200 OK` — request succeeded, response body contains result
- `201 Created` — POST succeeded, new resource created; `Location` header points to it
- `202 Accepted` — asynchronous operation accepted, processing not yet complete
- `204 No Content` — succeeded, no response body (common for DELETE or PUT)

**3xx — Redirection:**
- `301 Moved Permanently` — URI changed permanently; client should update bookmarks
- `302 Found` — URI changed temporarily
- `304 Not Modified` — cached response still valid (conditional GET with If-None-Match / ETag)

**4xx — Client errors:**
- `400 Bad Request` — invalid input (malformed JSON, missing required field)
- `401 Unauthorized` — authentication required or failed (missing/invalid token)
- `403 Forbidden` — authenticated but not authorized for this resource
- `404 Not Found` — resource does not exist
- `405 Method Not Allowed` — HTTP verb not supported for this endpoint
- `409 Conflict` — state conflict (duplicate key, resource locked)
- `415 Unsupported Media Type` — content-type not accepted
- `422 Unprocessable Entity` — syntactically valid but semantically invalid (business rule violation)
- `429 Too Many Requests` — rate limit exceeded

**5xx — Server errors:**
- `500 Internal Server Error` — unexpected server fault
- `502 Bad Gateway` — upstream service returned invalid response
- `503 Service Unavailable` — server is overloaded or down

**In C# (Minimal API):**
```csharp
Results.Ok(dto)              // 200
Results.Created($"/listings/{id}", dto)  // 201
Results.NoContent()          // 204
Results.BadRequest("...")    // 400
Results.Unauthorized()       // 401
Results.Forbid()             // 403
Results.NotFound()           // 404
Results.Conflict("...")      // 409
Results.UnprocessableEntity("...") // 422
Results.Problem(...)         // 500 (ProblemDetails)
```

---

## API Versioning

Three strategies:

**URI versioning:** `/api/v1/...` and `/api/v2/...`. Explicit, cache-friendly, easy to route. Breaking changes get a new prefix. MDH.AnalyticsApi uses this. Cons: URIs should identify resources, not API version — some argue this violates REST principles.

**Header versioning:** `Accept-Version: v2` or `api-version: 2.0`. Cleaner URIs, but not cache-friendly and harder to test manually (can't just paste URL in browser). Microsoft's Azure REST APIs use header versioning.

**Query parameter versioning:** `?api-version=2024-06-01`. Easy to test, easy to log. Used by Azure APIs. Less visually clean.

We use URI versioning because it is the simplest, most debuggable, and most widely understood choice for a demo API.

**The URI versioning rule:** A version bump is required only for breaking changes — removing fields, changing types, changing semantics. Adding new optional fields to a response is backwards-compatible and does not require a version bump.

---

## Error Responses — ProblemDetails (RFC 7807)

`ProblemDetails` is the standardized JSON error schema:

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "An error occurred while processing your request.",
  "status": 500,
  "traceId": "00-84c1...",
  "errors": { "submarket": ["'submarket' is required."] }
}
```

In ASP.NET Core 7+, `AddProblemDetails()` is on by default for controller APIs. For Minimal APIs, call `Results.Problem(detail, title, statusCode)` or `Results.ValidationProblem(errors)`.

MDH.AnalyticsApi does not currently add `AddProblemDetails()` middleware for global error handling — a production gap. Adding it:

```csharp
builder.Services.AddProblemDetails();
app.UseExceptionHandler(); // catches unhandled exceptions → ProblemDetails
```

---

## Pagination, Filtering, Sorting Conventions

**Offset pagination:** `?page=1&pageSize=25`  
**Keyset pagination:** `?after=<cursor>&pageSize=25`  
**Response envelope:**
```json
{
  "items": [...],
  "totalCount": 1284,
  "page": 1,
  "pageSize": 25
}
```

`MDH.AnalyticsApi` returns `PagedResult<T>` (in `SearchListingsQuery.cs` line 13) with `Items`, `TotalCount`, `Page`, `PageSize`.

**Stable sort key:** Any paginated endpoint must specify a stable sort order. Without it, items can appear on multiple pages or be skipped entirely as the underlying data changes between page requests. `ORDER BY LastUpdatedAt DESC, ListingId ASC` is stable — the secondary sort on GUID is a tiebreaker.

---

## Security Basics

**TLS everywhere.** All HTTP must be TLS in production. HTTP → HTTPS redirect is mandatory. Azure Container Apps enforce HTTPS for external ingress by default.

**JWT Bearer (HS256).** Token validation is stateless — the server verifies the signature, no database lookup. The token carries claims (`sub`, `iat`, `exp`) as payload. `exp` ensures tokens expire.

**Rate limiting.** ASP.NET Core 7+ has built-in rate limiting middleware (`AddRateLimiter`). Not implemented in this project — production gap. For a public API, rate limiting protects against abuse and cost overrun.

**API keys.** For server-to-server calls where a human is not involved (machine-to-machine), an API key in a header (`X-API-Key`) is simpler than JWT. JWT is better for user-initiated requests (short-lived, refreshable).

---

## HATEOAS — Why We Skip It

HATEOAS (Hypermedia As The Engine Of Application State) means responses include links to related actions:

```json
{
  "listingId": "...",
  "_links": {
    "self": { "href": "/api/v1/listings/123" },
    "anomalies": { "href": "/api/v1/listings/123/anomalies" },
    "submarket": { "href": "/api/v1/markets/Austin/metrics" }
  }
}
```

In theory this lets clients navigate the API without hardcoding URIs. In practice, every known client still hardcodes the initial entry point URI, and the link-following logic is complex to implement and test. The industry pragmatic consensus: implement Level 2 (HTTP verbs + correct status codes) and stop there. HATEOAS is academically interesting and practically rare.

---

## Exercise

1. MDH.AnalyticsApi returns a flat 200 for all successful GETs. Which endpoint should return 201 when listing creation is added, and what should the `Location` header contain?

2. A request arrives at `GET /api/v1/listings/{id:guid}` with a valid GUID that does not exist in the database. Open `GetListingByIdQuery.cs` and trace the code path that produces the 404 response.

3. The `SearchListingsQuery` returns a `PagedResult<ListingDto>`. Write the `PagedResult<T>` JSON response envelope and explain why `TotalCount` is needed even though the client already has the `Items` array.

4. A colleague proposes changing `GET /api/v1/markets/{submarket}/metrics` to `POST /api/v1/metrics` with the submarket in the body. List three reasons why GET with path parameter is preferable.

---

## Common mistakes

- **Using verbs in URIs.** `/api/getMarkets`, `/api/deleteListing/1`. URIs identify resources; HTTP methods are the verbs. The action is implicit in the method.

- **Returning 200 for errors.** Some teams return `{ success: false, message: "Not found" }` with status 200. HTTP consumers (browsers, proxies, CDNs) use status codes for routing and caching. A 404 tells the CDN not to cache; a 200 with an error body looks like a valid cacheable response.

- **401 vs 403 confusion.** 401 = "you are not authenticated" (no valid token or wrong token). 403 = "you are authenticated but not authorized" (valid token, wrong permissions). The `WWW-Authenticate` header must be present on 401 responses.

- **Missing `Content-Type` validation.** If a POST endpoint expects `application/json` and receives `text/xml`, return 415 Unsupported Media Type. ASP.NET Core's model binding does this automatically for controller APIs; with Minimal APIs you rely on the JSON body binding throwing `BadHttpRequestException`.

- **No pagination on list endpoints.** `GET /api/v1/listings` without pagination on a table with 1M rows will time out or return a 100MB response. Every collection endpoint must be paginated.

---

## Interview Angle — Smart Apartment Data

1. **"What Richardson Maturity Model level is your API?"** — Level 2: resources identified by URI, HTTP methods used semantically (GET for reads), meaningful status codes (200, 404, 401). We skip Level 3 (HATEOAS) deliberately — client complexity rarely justifies the benefit.

2. **"Why does GET /listings/search use query parameters instead of a request body?"** — GET is semantically correct (read, no side effects), results can be cached by the HTTP cache layer, the URL is bookmarkable and loggable. The main downside is URL length limits (typically 2048 characters), which is not a constraint for our filter parameters.

3. **"What is the difference between 401 and 403?"** — 401: not authenticated (no token or invalid token). 403: authenticated but not authorized (valid token, insufficient permissions). A common mistake is returning 403 when the user is not logged in — that should be 401.

4. **"How do you handle breaking API changes?"** — Introduce a new URI version (`/api/v2/...`). The old version remains active until clients migrate. Never change the semantics of existing endpoints — adding new optional response fields is backwards-compatible; removing fields or changing types requires a version bump.

5. **"Describe ProblemDetails."** — RFC 7807 defines a standard JSON error response format with `type`, `title`, `status`, `detail`, and `instance` fields. It ensures all errors from all endpoints have a consistent shape that clients can deserialize with a single error type. ASP.NET Core 7+ enables it by default for controllers; Minimal APIs use `Results.Problem(...)`.

6. **30-second talking point:** "The API lives at Richardson Maturity Level 2: resource-oriented URIs, semantic HTTP methods, and correct status codes. URI versioning with /api/v1/ is explicit and cache-friendly. Pagination uses offset with a stable sort key. JWT Bearer auth is applied at the route-group level so every endpoint in /api/v1/ requires authentication with one line of code. I know the tradeoffs at each decision point: offset vs keyset pagination, URI vs header versioning, GET vs POST for search."

7. **Job requirement proof:** "REST API design with C#" — primary requirement, directly demonstrated by 6 endpoints in MDH.AnalyticsApi with documented design choices.
