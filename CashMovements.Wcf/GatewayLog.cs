using Microsoft.Extensions.Logging;

namespace CashMovements.Wcf;

/// <summary>
/// The gateway's structured log events (4xxx). Every entry is written inside a scope carrying <c>Operation</c> and the
/// <c>CorrelationId</c> that is also sent to the gRPC server, so one legacy call can be followed into the server's logs.
/// Account IDs are masked; amounts are never logged.
/// </summary>
internal static partial class GatewayLog
{
    [LoggerMessage(EventId = 4001, EventName = "BalanceReturned", Level = LogLevel.Information,
        Message = "GetBalance for account {Account} in {Currency} returned in {ElapsedMs:0} ms.")]
    public static partial void BalanceReturned(ILogger logger, string account, string currency, double elapsedMs);

    [LoggerMessage(EventId = 4002, EventName = "BalanceRejected", Level = LogLevel.Information,
        Message = "GetBalance for account {Account} rejected as invalid input: {Reason}")]
    public static partial void BalanceRejected(ILogger logger, string account, string reason);

    [LoggerMessage(EventId = 4003, EventName = "BackendUnavailable", Level = LogLevel.Warning,
        Message = "GetBalance for account {Account}: backend unavailable ({StatusCode}: {Detail}) after {ElapsedMs:0} ms.")]
    public static partial void BackendUnavailable(ILogger logger, string account, string statusCode, string detail, double elapsedMs);

    [LoggerMessage(EventId = 4004, EventName = "BalanceFailed", Level = LogLevel.Error,
        Message = "GetBalance for account {Account} failed.")]
    public static partial void BalanceFailed(ILogger logger, Exception exception, string account);

    /// <summary>Only the last 4 characters: <c>ACC-1001</c> becomes <c>****1001</c>; 4 characters or fewer become <c>****</c>.</summary>
    public static string MaskAccount(string? accountId)
    {
        var trimmed = accountId?.Trim();
        return trimmed is null || trimmed.Length <= 4 ? "****" : "****" + trimmed.Substring(trimmed.Length - 4);
    }
}
