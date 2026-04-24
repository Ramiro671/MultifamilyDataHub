namespace MDH.Shared.Contracts;

public interface IRawListingStore
{
    Task InsertManyAsync(IEnumerable<object> documents, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetUnprocessedAsync<T>(int batchSize, CancellationToken ct = default);
    Task MarkProcessedAsync(IEnumerable<string> ids, CancellationToken ct = default);
}
