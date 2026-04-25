# 10 — MDH.InsightsService — AI Integration with Claude

> **Study time:** ~30 minutes
> **Prerequisites:** [`09_MDH_ANALYTICSAPI.md`](./09_MDH_ANALYTICSAPI.md)

## Why this matters

"AI tools in development workflow" is listed as a PLUS in the Smart Apartment Data JD. InsightsService delivers on that plus in two ways: (1) runtime AI — the service calls the Anthropic Claude API to generate natural-language market summaries and listing explanations; (2) development AI — Claude Code generated and maintains the entire codebase. Both are worth explaining explicitly in an interview.

Beyond the AI angle, this service is the canonical example of calling an unreliable external API from a production service: typed HttpClient, Polly retry + circuit breaker, prompt templates externalised to Markdown files, cost-aware response caching. These patterns apply to any third-party API integration.

By the end of this doc you will be able to: (1) explain why InsightsService is a separate service from AnalyticsApi and what the circuit breaker protects; (2) describe the Polly retry + circuit breaker policies and their state machine; (3) trace a `POST /api/v1/insights/market-summary` request from HTTP to Claude response.

---

## Why AI Lives in Its Own Service

Anthropic API calls have characteristics that differ fundamentally from warehouse reads:

- **Latency:** 500ms–3s per request (vs 10–50ms for SQL). Serving both from the same process would drag down API response times.
- **Cost:** Each call consumes tokens (input + output). If InsightsService has a bug that sends infinite requests, it burns Anthropic quota. Isolation limits blast radius.
- **Availability:** Anthropic has scheduled maintenance and occasional 5xx incidents. If insights are served from the same process as warehouse queries, a Claude outage returns 503 for `/markets` too — which is completely unacceptable. The circuit breaker in this service isolates that failure.
- **Independent scaling:** Insights are expensive; you want fewer replicas. Analytics queries are cheap; you want more replicas. Same process = same scaling unit.

---

## Anthropic Messages API

`src/MDH.InsightsService/Anthropic/AnthropicClient.cs` makes a direct `HttpClient` call:

```csharp
var requestBody = new {
    model = "claude-sonnet-4-20250514",
    max_tokens = 1024,
    system = systemPrompt,
    messages = new[] { new { role = "user", content = userMessage } }
};

var response = await _httpClient.PostAsync(
    "https://api.anthropic.com/v1/messages", content, ct);
response.EnsureSuccessStatusCode();
```

Key parameters:
- **`model`:** `claude-sonnet-4-20250514` — Sonnet 4 (latest as of writing). Balances capability and cost.
- **`max_tokens`:** 1024 — maximum tokens in the response. One token ≈ 0.75 English words, so 1024 tokens ≈ 750 words. Sufficient for a 200-300 word market summary.
- **`system`:** The system prompt loaded from `Prompts/market-summary.md`. Sets the persona and output format.
- **`messages`:** A single user message with the serialized market data JSON.

We use **non-streaming** because the downstream API response is assembled and returned in one piece. For a chatbot use case, streaming (`"stream": true`) would send tokens as they are generated, reducing time-to-first-token. The complexity cost (SSE or WebSocket handling in C#) is not justified for batch market summaries.

---

## Typed HttpClient with `IHttpClientFactory`

```csharp
// Program.cs lines 47-54
builder.Services.AddHttpClient<AnthropicClient>(client => {
    client.BaseAddress = new Uri("https://api.anthropic.com");
    client.DefaultRequestHeaders.Add("x-api-key", apiKey);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
})
.AddPolicyHandler(GetRetryPolicy())
.AddPolicyHandler(GetCircuitBreakerPolicy());
```

`IHttpClientFactory` manages `HttpMessageHandler` lifetimes. The classic `new HttpClient()` mistake: `HttpClient` is `IDisposable` but you must NOT dispose it per-request — doing so rapidly exhausts socket handles (`TIME_WAIT` state). `IHttpClientFactory` solves this by pooling handlers independently of client lifetimes. The typed client `AnthropicClient` gets a fresh `HttpClient` instance (with a shared, pooled handler) via DI each time it is resolved.

`DefaultRequestHeaders` are set once on registration and applied to every request. The `x-api-key` header carries the Anthropic API key. The `anthropic-version` header is required by the Anthropic API and specifies the API version for stability.

---

## Polly Policies in Detail

### Retry Policy

```csharp
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt =>
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
```

`HandleTransientHttpError()` matches `HttpRequestException` and HTTP 5xx + 408 (Request Timeout). The retry waits: 2s, 4s, 8s before giving up. Total wait on full failure: 14 seconds. This is exponential backoff — standard industry practice to avoid hammering a recovering service.

**Production improvement:** Add jitter — `TimeSpan.FromSeconds(Math.Pow(2, retryAttempt) + random.NextDouble())`. Without jitter, all instances retry at the same moment after a transient fault (the "thundering herd" problem). Jitter spreads retries across a window.

### Circuit Breaker Policy

```csharp
static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy() =>
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
```

State machine:
- **Closed (normal):** Requests flow through. Each failure increments a counter.
- **Open (tripped):** After 5 consecutive failures, the circuit opens. All requests immediately throw `BrokenCircuitException` — no call to Anthropic, no wasted time waiting for timeouts.
- **Half-Open (recovering):** After 30 seconds, one trial request is sent. If it succeeds, the circuit closes. If it fails, the circuit opens again for another 30s.

**Why circuit breaker matters:** Without it, a 3-second Anthropic timeout × 3 retries = 9 seconds of waiting per request, with all concurrent requests queueing behind each other. Thread pool exhaustion follows. With circuit breaker, after 5 failures the remaining requests fail fast (< 1ms) and the system stays responsive.

---

## Prompt Engineering

Prompts live in `src/MDH.InsightsService/Prompts/*.md`:

**`market-summary.md`** defines:
- Role assignment: "You are an expert multifamily real estate analyst"
- Structured data injection: the market data JSON is injected into `userMessage`
- Output format constraint: "Respond with a JSON object with fields: summary, keyStats, sentiment"

Constraining the output to JSON is the most important prompt engineering decision here. It prevents the model from responding in free-form prose that the caller cannot parse, and it lets the response be serialized back into the `InsightResponseDto` contract.

**`listing-explain.md`** defines:
- Context framing: listing details + anomaly data
- Tone guidance: "Write for a property manager or investor, not a statistician"
- Output format: `{ explanation, standoutReason, riskLevel }`

Externalising prompts to Markdown files (rather than hardcoding strings in C#) has two benefits: (1) they are readable and editable by non-engineers (product managers, domain experts); (2) they are visible in the repo as explicit artifacts demonstrating AI integration to a reviewer.

---

## Request Trace — `POST /api/v1/insights/market-summary`

```
1. HTTP POST arrives at InsightsService (port 5040)
   Body: { submarket: "Austin", fromDate: "2024-06-01", toDate: "2024-06-30" }

2. JWT middleware validates Bearer token — 401 if invalid

3. MarketSummaryEndpoint.HandleAsync() invoked
   src/MDH.InsightsService/Features/MarketSummaryEndpoint.cs

4. IHttpClientFactory.CreateClient("AnalyticsApi") → HttpClient with Bearer token
   GET https://ca-mdh-analytics-api.../api/v1/markets/Austin/metrics?from=...&to=...
   → Returns List<MarketMetricsDto> JSON

5. Serialise metrics to JSON → inject into userMessage

6. LoadPromptTemplate("market-summary") reads Prompts/market-summary.md

7. AnthropicClient.SendMessageAsync(systemPrompt, userMessage)
   // 🔴 BREAKPOINT: prompt assembled, sending to Claude
   POST https://api.anthropic.com/v1/messages
   Polly retry policy wraps this call

8. Claude responds: { summary: "...", keyStats: {...}, sentiment: "bullish" }

9. InsightResponseDto assembled with Claude text + raw stats + timestamp
   HTTP 200 OK
```

---

## Cost and Latency

**Input tokens:** system prompt (~300 tokens) + user message (market JSON, ~200 tokens) = ~500 input tokens.
**Output tokens:** market summary (~300 words ≈ 400 tokens).
**Cost:** ~0.003 USD per request at Sonnet 4 pricing (varies by model version).

**Caching opportunity:** Market summaries for the same submarket + date range are identical across requests. A response cache keyed on `(submarket, fromDate, toDate)` would reduce both cost and latency. Current implementation: no caching (every request calls Claude). Production improvement: `IMemoryCache` or `IDistributedCache` with a 5-minute TTL.

---

## The Double AI Story

Two distinct AI use cases are present in this project:

1. **Runtime AI** (InsightsService): Claude generates market summaries and listing explanations at request time. This is an AI-powered feature.

2. **Development AI** (Claude Code): The entire codebase — migrations, Bicep templates, Dockerfiles, tests, and these docs — was generated and iteratively refined by Claude Code (me). This demonstrates the "AI tools in development workflow" plus that the JD specifically calls out.

In an interview, mention both explicitly: "InsightsService calls the Anthropic API at runtime, and I used Claude Code throughout development to accelerate everything from scaffolding to debugging the Azure deployment."

---

## Exercise

1. Open `AnthropicClient.cs` line 18: `// 🔴 BREAKPOINT: prompt assembled, sending to Claude`. Set a breakpoint here and inspect `systemPrompt` and `userMessage`. What would you add to `systemPrompt` to make Claude always respond in Spanish?

2. The circuit breaker opens after 5 consecutive failures. What happens to the 6th request? What HTTP status code does the API return to the caller?

3. The retry policy does not include `HttpStatusCode.TooManyRequests` (429) in `HandleTransientHttpError()`. Anthropic returns 429 when you exceed the rate limit. What would you add to handle 429 with a 60-second wait?

4. The prompt templates are loaded with `File.ReadAllText(path)` at request time. What is the performance implication? How would you optimize it for high-traffic scenarios?

---

## Common mistakes

- **Storing `HttpClient` as a static field or singleton without `IHttpClientFactory`.** Static `HttpClient` does not respect DNS changes (a problem with Azure services that rotate IPs). `IHttpClientFactory` refreshes handlers periodically. Always use the factory.

- **Hardcoding the API key in code or appsettings.json.** The key must come from env vars or Key Vault at runtime. The code correctly reads from `Anthropic:ApiKey` config (which comes from Container Apps secrets) with a fallback to `ANTHROPIC_API_KEY` env var. Never commit API keys.

- **No circuit breaker on external API calls.** Without circuit breaker, a slow Anthropic response ties up threads for 3s per request × retry count = 9s per request. At 10 concurrent requests, all 10 threads stall, thread pool queue grows, the entire service becomes unresponsive.

- **Trusting Claude output without validation.** Claude is instructed to return JSON but occasionally returns prose wrapping JSON, or a JSON object with extra commentary. `JsonSerializer.Deserialize()` will throw. Add a try/catch with a graceful degradation fallback (return raw text if JSON parse fails).

- **Exposing Claude errors to the caller.** A 401 from Anthropic (invalid API key) should return a 502 or 503 to the caller with a generic message, not "Unauthorized: your API key is invalid." Leaking upstream error details is both a security concern and a bad UX.

---

## Interview Angle — Smart Apartment Data

1. **"Why is InsightsService a separate service from AnalyticsApi?"** — Isolation: AI calls are slow (1–3s), expensive (per-token cost), and depend on an external service with its own SLA. If Claude is down, it should not take down warehouse queries. The circuit breaker in InsightsService ensures that a Claude outage fails fast locally without cascading to the analytics layer.

2. **"Explain the Polly circuit breaker."** — After 5 consecutive failures, the circuit opens. All subsequent requests fail immediately (BrokenCircuitException) without calling Anthropic — no timeout wait, no thread stall. After 30 seconds, one probe request is sent. If it succeeds, the circuit closes. If it fails, it opens again. This prevents the thundering herd when a recovering service is hit by a wave of retried requests.

3. **"How do you manage Anthropic API keys?"** — In production (Azure): stored as a Container App secret, injected as env var `Anthropic__ApiKey`. The `__` double-underscore is the .NET config key delimiter for nested sections. `builder.Configuration["Anthropic:ApiKey"]` reads it. The key is never in code or appsettings.json.

4. **"What would you add to make InsightsService production-ready?"** — Response caching by (submarket, dateRange) hash to reduce cost and latency; streaming responses for lower time-to-first-token; rate limiting to prevent cost overruns; distributed tracing to correlate Claude latency with business metrics.

5. **"Tell me about AI tools in your development workflow."** — Two levels: InsightsService calls Claude at runtime for market analysis (AI as a product feature). Claude Code (Anthropic's CLI) generated and maintained the entire codebase — scaffolding, migrations, Bicep templates, Dockerfiles, tests, debugging the Azure deployment, and writing this curriculum. That's AI in the development workflow.

6. **30-second talking point:** "InsightsService calls the Anthropic Claude API to generate natural-language market summaries and listing explanations. It's isolated from AnalyticsApi so a Claude outage can't take down warehouse reads. Polly provides retry with exponential backoff and a circuit breaker that fails fast after 5 consecutive failures. Prompt templates are Markdown files in the repo — visible to non-engineers and reviewers. The same Claude model that powers this service also generated the entire codebase through Claude Code."

7. **Job requirement proof:** "AI tools in workflow (PLUS)" — runtime Anthropic API integration + development-time Claude Code usage. Both explicitly documented.
