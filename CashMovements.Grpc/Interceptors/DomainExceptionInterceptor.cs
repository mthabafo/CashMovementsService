using CashMovements.Domain.Exceptions;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace CashMovements.Grpc.Interceptors;

/// <summary>
/// Maps exceptions to gRPC status codes in one place, so service methods need no try/catch:
/// <list type="bullet">
/// <item><see cref="DuplicateMovementException"/> → ALREADY_EXISTS</item>
/// <item>any other <see cref="DomainException"/> (invalid value or movement) → INVALID_ARGUMENT</item>
/// <item><see cref="RpcException"/> and cancellations → passed through unchanged</item>
/// <item>anything else → INTERNAL, logged, with no details leaked to the client</item>
/// </list>
/// Also covers server streaming, including failures part-way through a statement.
/// </summary>
public sealed class DomainExceptionInterceptor : Interceptor
{
    private readonly ILogger<DomainExceptionInterceptor> _logger;

    public DomainExceptionInterceptor(ILogger<DomainExceptionInterceptor> logger) => _logger = logger;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex) when (ShouldTranslate(ex))
        {
            throw Translate(ex, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (Exception ex) when (ShouldTranslate(ex))
        {
            throw Translate(ex, context);
        }
    }

    private static bool ShouldTranslate(Exception ex)
        => ex is not RpcException and not OperationCanceledException;

    private RpcException Translate(Exception ex, ServerCallContext context)
    {
        switch (ex)
        {
            case DuplicateMovementException:
                return new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));

            case DomainException:
                return new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));

            default:
                _logger.LogError(ex, "Unhandled exception in gRPC call {Method}", context.Method);
                return new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred."));
        }
    }
}
