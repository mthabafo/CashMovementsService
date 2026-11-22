using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace CashMovements.Grpc.Interceptors;

/// <summary>
/// One log scope and one summary line per gRPC call. Registered before <see cref="DomainExceptionInterceptor"/>, so it
/// runs outermost and sees the final status code.
/// <list type="bullet">
/// <item>Scope: every log written during the call (business events, retries, errors) carries <c>GrpcMethod</c> and
/// <c>CorrelationId</c>, so one call can be followed through the logs.</item>
/// <item>Correlation ID: taken from the caller's <c>x-correlation-id</c> header (the WCF gateway sends one), or generated,
/// and returned in the response trailers.</item>
/// <item>Summary: <c>GrpcCallCompleted</c> (event 2001) with the method, status code and duration.</item>
/// </list>
/// </summary>
public sealed partial class CallLoggingInterceptor : Interceptor
{
    public const string CorrelationHeader = "x-correlation-id";

    private const int MaxCorrelationIdLength = 64;

    private readonly ILogger<CallLoggingInterceptor> _logger;

    public CallLoggingInterceptor(ILogger<CallLoggingInterceptor> logger) => _logger = logger;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        using var scope = BeginCall(context, out var started);
        try
        {
            var response = await continuation(request, context);
            Completed(context, StatusCode.OK, started);
            return response;
        }
        catch (Exception ex)
        {
            Completed(context, StatusOf(ex), started);
            throw;
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        using var scope = BeginCall(context, out var started);
        try
        {
            await continuation(request, responseStream, context);
            Completed(context, StatusCode.OK, started);
        }
        catch (Exception ex)
        {
            Completed(context, StatusOf(ex), started);
            throw;
        }
    }

    private IDisposable? BeginCall(ServerCallContext context, out long started)
    {
        started = Stopwatch.GetTimestamp();

        var correlationId = CorrelationIdFrom(context.RequestHeaders) ?? Guid.NewGuid().ToString("N");
        context.ResponseTrailers.Add(CorrelationHeader, correlationId);

        return _logger.BeginScope(new Dictionary<string, object>
        {
            ["GrpcMethod"] = context.Method,
            ["CorrelationId"] = correlationId,
        });
    }

    private void Completed(ServerCallContext context, StatusCode status, long started)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // Server-side failures stand out; everything else (including the caller's own mistakes) is routine.
        var level = status is StatusCode.Internal or StatusCode.Unknown or StatusCode.Unavailable or StatusCode.DataLoss
            ? LogLevel.Warning
            : LogLevel.Information;

        CallCompleted(_logger, level, context.Method, status, elapsedMs);
    }

    private static StatusCode StatusOf(Exception ex) => ex switch
    {
        RpcException rpc => rpc.StatusCode,
        OperationCanceledException => StatusCode.Cancelled,
        _ => StatusCode.Unknown,
    };

    /// <summary>
    /// The caller's correlation ID, if it's safe to log: up to 64 letters, digits, '-', '_' or '.'. Anything else (which
    /// could be used to forge log lines) is ignored and a new ID is generated.
    /// </summary>
    private static string? CorrelationIdFrom(Metadata headers)
    {
        var value = headers.GetValue(CorrelationHeader);
        if (string.IsNullOrEmpty(value) || value.Length > MaxCorrelationIdLength)
            return null;

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? value : null;
    }

    [LoggerMessage(EventId = 2001, EventName = "GrpcCallCompleted",
        Message = "gRPC {GrpcMethod} finished with {StatusCode} in {ElapsedMs:0} ms.")]
    private static partial void CallCompleted(ILogger logger, LogLevel level, string grpcMethod, StatusCode statusCode, double elapsedMs);
}
