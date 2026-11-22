using Microsoft.Extensions.Logging;

namespace CashMovements.Application.Logging;

/// <summary>
/// The application's business events, as structured log entries with fixed event IDs (1xxx) that dashboards and alerts
/// can filter on. Every placeholder becomes a named field in JSON output.
/// <para>
/// Privacy: account IDs are always masked (<see cref="LogMask.Account"/>), and amounts and narrations are never logged.
/// The database is the financial record; logs are seen by many more people.
/// </para>
/// </summary>
internal static partial class ApplicationLog
{
    [LoggerMessage(EventId = 1001, EventName = "MovementRecorded", Level = LogLevel.Information,
        Message = "Movement {MovementId} recorded on account {Account} with reference {ExternalRef} in {Currency}.")]
    public static partial void MovementRecorded(ILogger logger, long movementId, string account, string externalRef, string currency);

    [LoggerMessage(EventId = 1002, EventName = "MovementReplayed", Level = LogLevel.Information,
        Message = "Re-submission of reference {ExternalRef} on account {Account} returned the original movement {MovementId} (lost insert race: {LostInsertRace}).")]
    public static partial void MovementReplayed(ILogger logger, string externalRef, string account, long movementId, bool lostInsertRace);

    [LoggerMessage(EventId = 1003, EventName = "MovementConflict", Level = LogLevel.Warning,
        Message = "Reference {ExternalRef} on account {Account} is already recorded with a different {ConflictingFields}; the submission was rejected.")]
    public static partial void MovementConflict(ILogger logger, string externalRef, string account, string conflictingFields);

    [LoggerMessage(EventId = 1101, EventName = "StatementCompleted", Level = LogLevel.Information,
        Message = "Statement for account {Account} in {Currency} from {From:o} to {To:o} completed: {LineCount} lines in {ElapsedMs:0} ms.")]
    public static partial void StatementCompleted(ILogger logger, string account, string currency, DateTimeOffset from, DateTimeOffset to, int lineCount, double elapsedMs);

    [LoggerMessage(EventId = 1102, EventName = "StatementEndedEarly", Level = LogLevel.Warning,
        Message = "Statement for account {Account} in {Currency} from {From:o} to {To:o} ended early ({Reason}) after {LineCount} lines in {ElapsedMs:0} ms.")]
    public static partial void StatementEndedEarly(ILogger logger, string account, string currency, DateTimeOffset from, DateTimeOffset to, string reason, int lineCount, double elapsedMs);
}

/// <summary>Masks identifiers before they're logged. Public so every layer masks the same way.</summary>
public static class LogMask
{
    /// <summary>Only the last 4 characters: <c>ACC-1001</c> becomes <c>****1001</c>; 4 characters or fewer become <c>****</c>.</summary>
    public static string Account(string? accountId)
        => accountId is null || accountId.Length <= 4 ? "****" : "****" + accountId.Substring(accountId.Length - 4);
}
