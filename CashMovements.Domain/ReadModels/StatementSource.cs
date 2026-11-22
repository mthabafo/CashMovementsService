using CashMovements.Domain.Entities;

namespace CashMovements.Domain.ReadModels;

/// <summary>
/// The raw data for one statement, read from a single consistent snapshot: the opening balance and the movements
/// in the period. Whatever holds the snapshot open (for SQL Server, a connection and a SNAPSHOT transaction) stays
/// open until this is disposed, so always dispose it, even if <see cref="Movements"/> is never enumerated.
/// </summary>
public sealed class StatementSource : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<int>>? _countLines;
    private IAsyncDisposable? _resources;

    /// <param name="openingBalance">Sum of movements that occurred before the period.</param>
    /// <param name="movements">Movements in the period, ordered by OccurredAt then Id. Enumerable once.</param>
    /// <param name="resources">Released on dispose; null when nothing needs releasing.</param>
    /// <param name="countLines">Counts the movements in the period from the same snapshot; null if not supported.</param>
    public StatementSource(
        decimal openingBalance,
        IAsyncEnumerable<Movement> movements,
        IAsyncDisposable? resources = null,
        Func<CancellationToken, Task<int>>? countLines = null)
    {
        OpeningBalance = openingBalance;
        Movements = movements ?? throw new ArgumentNullException(nameof(movements));
        _resources = resources;
        _countLines = countLines;
    }

    public decimal OpeningBalance { get; }

    public IAsyncEnumerable<Movement> Movements { get; }

    /// <summary>
    /// The number of movements <see cref="Movements"/> will yield, from the same snapshot, so it always matches (for
    /// example, an export's progress total). Call it before enumerating <see cref="Movements"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">This source can't count its lines.</exception>
    public Task<int> CountLinesAsync(CancellationToken cancellationToken)
        => _countLines is null
            ? throw new NotSupportedException("This statement source can't count its lines.")
            : _countLines(cancellationToken);

    /// <summary>Releases the snapshot. Safe to call more than once.</summary>
    public ValueTask DisposeAsync()
    {
        var resources = Interlocked.Exchange(ref _resources, null);
        return resources?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
