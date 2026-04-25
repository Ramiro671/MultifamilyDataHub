# 06 — MDH.Shared — The Shared Library

> **Study time:** ~20 minutes
> **Prerequisites:** [`01_PROJECT_STRUCTURE.md`](./01_PROJECT_STRUCTURE.md)

## Why this matters

A shared library in a microservices repo is both an architectural asset and an architectural risk. Done well, it gives all services a common vocabulary (DTOs, contracts) without coupling them to each other's implementation details. Done badly, it becomes a dumping ground for infrastructure code, business logic, and service-specific concerns — and every change requires redeployment of every service.

Understanding the boundary of what belongs in `MDH.Shared` vs what belongs in each service is an architectural judgment call that comes up in every design review. You need a principled answer, not a "put it wherever seems convenient" answer.

By the end of this doc you will be able to: (1) name every public type in `MDH.Shared` and its purpose; (2) explain the `Result<T>` pattern and show two concrete usage sites; (3) articulate what does and does not belong in a shared library.

---

## The Purpose of a Shared Library

In a microservices repo where services are compiled together (monorepo, project references), `MDH.Shared` serves as the **lingua franca** — the common language. It defines:
- **Domain primitives** — types that encode domain rules and prevent invalid states
- **DTOs** — the data shapes that cross service boundaries via HTTP
- **Contracts** — interfaces that define behavior without coupling to implementation

The key constraint: `MDH.Shared` depends on nothing except the .NET BCL. It has no EF Core, no MongoDB, no Hangfire, no ASP.NET Core. If you added `WarehouseDbContext` to `MDH.Shared`, every service that references shared would pull in `Microsoft.EntityFrameworkCore.SqlServer` as a transitive dependency — even the ones that don't use SQL. This is the "depends-on-none" rule.

---

## File-by-File Walkthrough

### `src/MDH.Shared/Domain/Money.cs`

```csharp
public readonly record struct Money(decimal Amount, string Currency = "USD")
{
    public Money Add(Money other) { ... }
    public Money Subtract(Money other) { ... }
    public Money Multiply(decimal factor) => new(Amount * factor, Currency);
    public static Money operator +(Money a, Money b) => a.Add(b);
    // ...
}
```

`Money` is a **domain primitive** — a value type that wraps a `decimal` and enforces currency consistency. It exists to eliminate **primitive obsession**: the anti-pattern of using raw `decimal` everywhere, which allows mixing EUR and USD amounts without a compile-time error. With `Money`, the expression `usdRent + eurRent` throws an `InvalidOperationException` because `Add()` checks `Currency != other.Currency`.

The `readonly record struct` design is intentional: (1) `readonly` prevents mutation after construction; (2) `record` gives structural equality — `Money(2500, "USD") == Money(2500, "USD")` without overriding `Equals`; (3) `struct` avoids heap allocation — money comparisons are O(1) stack operations.

**Usage sites:** `BuildMarketMetricsJob` and `DetectAnomaliesJob` could use `Money` for rent aggregations instead of raw `decimal`. The fact that they use `decimal` directly today shows a pragmatic shortcut — the type exists to demonstrate the concept.

### `src/MDH.Shared/Domain/BedBath.cs`

```csharp
public readonly record struct BedBath(int Bedrooms, decimal Bathrooms)
{
    public override string ToString() => $"{Bedrooms}BR/{Bathrooms}BA";
}
```

A simple composite primitive encoding bedroom/bathroom configuration as a unit. Eliminates the bug class where you accidentally compare `Bedrooms` of one listing against `Bathrooms` of another. In a codebase that passes bedroom counts everywhere as `int`, this type makes the parameter semantics explicit at the call site.

### `src/MDH.Shared/Domain/Submarket.cs` and `ListingId.cs`

`Submarket` wraps the submarket name string and could validate against the known list of 12. `ListingId` wraps a GUID. Both are domain identifiers — they exist to prevent passing a `ListingId` where a `SubmarketId` is expected.

### `src/MDH.Shared/Common/Result.cs`

```csharp
public class Result<T>
{
    public bool IsSuccess { get; private init; }
    public T? Value { get; private init; }
    public string? Error { get; private init; }

    public static Result<T> Success(T value) => new() { IsSuccess = true, Value = value };
    public static Result<T> Failure(string error) => new() { IsSuccess = false, Error = error };

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<string, TOut> onFailure)
        => IsSuccess ? onSuccess(Value!) : onFailure(Error!);
}
```

`Result<T>` is the **railway-oriented programming** pattern. Instead of throwing exceptions for expected failure cases, methods return `Result<T>`. The caller must handle both branches via `Match()`. This keeps the happy path and error path symmetrical in code structure.

**When to use `Result<T>` vs exceptions:**
- Use `Result<T>` for expected failures that the caller can and should handle: validation errors, not-found, business rule violations.
- Use exceptions for unexpected failures: null dereferences, I/O errors, programming mistakes. These should propagate up to a global handler and produce a 500.

**Two concrete usage patterns:**

```csharp
// Pattern 1: return result from a handler
public Result<ListingDto> GetListing(Guid id) {
    var entity = _db.Find(id);
    if (entity == null)
        return Result<ListingDto>.Failure("Listing not found");
    return Result<ListingDto>.Success(MapToDto(entity));
}

// Pattern 2: match at the API boundary
var result = GetListing(id);
return result.Match(
    dto => Results.Ok(dto),
    error => Results.NotFound(new { error })
);
```

### `src/MDH.Shared/Contracts/IRawListingStore.cs`

```csharp
public interface IRawListingStore {
    Task InsertManyAsync(IEnumerable<object> documents, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetUnprocessedAsync<T>(int batchSize, CancellationToken ct = default);
    Task MarkProcessedAsync(IEnumerable<string> ids, CancellationToken ct = default);
}
```

`IRawListingStore` is a contract that both `IngestionService` (which writes) and `OrchestrationService` (which reads and marks processed) depend on. The concrete implementation (`RawListingStore`) lives in each service's infrastructure layer. This allows `OrchestrationService` to test `CleanListingsJob` with a mock `IRawListingStore` without spinning up a MongoDB instance.

### `src/MDH.Shared/DTOs/`

All DTOs used in HTTP responses are in `MDH.Shared`:
- `ListingDto` — full listing detail for API responses
- `MarketMetricsDto` — submarket metrics for analytics endpoints
- `AnomalyDto` — anomaly record for the flagging endpoint
- `InsightRequestDto` / `InsightResponseDto` — AI insight request/response contracts

DTOs are plain C# records with no behavior. They live in Shared because they cross the HTTP boundary between InsightsService and AnalyticsApi.

---

## Anti-Patterns: What NOT to Put in Shared

**Business logic.** `CleanListingsJob` normalization logic (trim, dedupe, submarket lookup) does not belong here. If it did, every service would pull in ETL dependencies.

**Infrastructure code.** `WarehouseDbContext`, `MongoClient`, `HttpClient` registrations — all service-private.

**Service-specific DTOs.** If only InsightsService uses a type, it belongs in `MDH.InsightsService`, not Shared.

**Configuration.** `IOptions<IngestionOptions>` belongs in IngestionService.

A useful test: if you deleted a type from Shared and only one service broke, the type probably belongs in that service, not Shared.

---

## Extending Shared — The Procedure

```bash
# 1. Add the type to the right namespace
# src/MDH.Shared/DTOs/NewReportDto.cs

# 2. Add a unit test in MDH.Shared.Tests
# tests/MDH.Shared.Tests/DTOs/NewReportDtoTests.cs

# 3. No version bump needed — project reference recompiles automatically
# 4. All referencing services pick up the change at the next build
```

The absence of a publish step is the key advantage of project references over NuGet packages. The compiler enforces that all consumers handle breaking changes immediately.

---

## Exercise

1. Open `src/MDH.Shared/Domain/Money.cs`. What happens at runtime if you call `new Money(2500).Add(new Money(100, "EUR"))`? Where in the code is this validated?

2. Open `src/MDH.Shared/Common/Result.cs`. Write a code snippet that calls `Result<ListingDto>.Failure("not found")` and converts it to an `IResult` using `Results.NotFound()` via the `Match` method.

3. `IRawListingStore.GetUnprocessedAsync<T>` uses a generic type parameter. Why is this design questionable, and how would you improve it?

4. Why is `BedBath` a `struct` rather than a `class`? What is the behavior difference for equality comparison?

---

## Common mistakes

- **Infrastructure in Shared.** Adding `DbContext` or `MongoClient` to `MDH.Shared` couples all services to infrastructure they may not use and creates circular dependency risks (Shared → EF Core → SQL Provider → vendor-specific code).

- **DTOs with behavior.** DTOs should be plain data carriers. Adding methods that perform business logic to a DTO conflates data transfer concerns with domain logic. The logic belongs in a domain service or handler.

- **Using `Result<T>` for exceptions.** `Result<T>` is for expected failures. Catching `SqlException` in a handler and returning `Result.Failure(ex.Message)` leaks infrastructure details to the caller. Unexpected exceptions should propagate — the global exception handler returns `500` to the client.

- **Throwing exceptions for flow control.** The inverse anti-pattern: `throw new ListingNotFoundException(id)` for a normal 404 case. This forces callers to use try/catch for a routine business condition. Use `Result<T>` or `return null` instead.

- **One giant shared library.** Large teams split shared into `MDH.Shared.Contracts` (interfaces), `MDH.Shared.DTOs` (data transfer), and `MDH.Shared.Domain` (primitives) to avoid transitive dependencies between layers. This project keeps them together in one library, which is fine for five services.

---

## Interview Angle — Smart Apartment Data

1. **"What belongs in a shared library in a microservices repo?"** — Contracts (interfaces), DTOs that cross service boundaries, and domain primitives. Infrastructure code, business logic, and service-specific types stay in the service that owns them. The rule: Shared depends on nothing except the BCL.

2. **"Explain Result<T> and when you use it instead of exceptions."** — `Result<T>` is the railway pattern. A method returns either a success with a value or a failure with an error message. Callers handle both branches explicitly via `Match()`. Use it for expected failures (not found, validation error, business rule violation). Use exceptions for unexpected failures (null reference, disk full, programming mistakes).

3. **"Why is Money a struct instead of a class?"** — Structs are value types allocated on the stack for small payloads. Two `Money` instances with the same amount and currency are equal by value (`record struct` gives structural equality for free) without overriding `Equals`. This means `Money(2500, "USD") == Money(2500, "USD")` is `true` without any custom equality code.

4. **"What would you put in Shared vs keep in the service?"** — In Shared: `IRawListingStore` (interface), `ListingDto` (DTO used by InsightsService to call AnalyticsApi). In the service: `RawListingStore` (concrete MongoDB implementation), `WarehouseDbContext` (EF Core), `CleanListingsJob` normalization logic.

5. **30-second talking point:** "MDH.Shared follows the 'depends-on-none' principle — it has no infrastructure dependencies, just domain primitives and contracts. This means IngestionService and OrchestrationService both reference the same IRawListingStore interface, but each has its own concrete implementation. Tests can inject a mock without spinning up MongoDB. The boundary between Shared and service-private code is the most important architectural line in the whole repo."

6. **Job requirement proof:** Domain-Driven Design / clean architecture principles — domain primitives with validation, Result pattern, interface-based contracts, strict separation of Shared from infrastructure.
