namespace CashMovements.UnitTests;

/// <summary>A <see cref="TimeProvider"/> frozen at a known instant, so date rules are deterministic.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
