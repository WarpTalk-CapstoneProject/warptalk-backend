using Grpc.Core;

namespace WarpTalk.Shared;

/// <summary>
/// Factory methods for creating standardized gRPC exceptions.
/// Eliminates duplicated error message strings across gRPC services.
/// </summary>
public static class GrpcErrors
{
    /// <summary>Throws InvalidArgument when a GUID cannot be parsed.</summary>
    public static RpcException InvalidId(string entityName)
        => new(new Status(StatusCode.InvalidArgument, $"Invalid {entityName} ID format."));

    /// <summary>Throws NotFound when an entity lookup returns null.</summary>
    public static RpcException NotFound(string entityName, string id)
        => new(new Status(StatusCode.NotFound, $"{entityName} with ID {id} not found."));

    /// <summary>Throws InvalidArgument when a required field is missing.</summary>
    public static RpcException Required(string fieldName)
        => new(new Status(StatusCode.InvalidArgument, $"{fieldName} is required."));

    /// <summary>Throws Unauthenticated for missing/invalid auth.</summary>
    public static RpcException Unauthenticated(string? message = null)
        => new(new Status(StatusCode.Unauthenticated, message ?? "Authentication required."));

    /// <summary>Throws PermissionDenied for insufficient permissions.</summary>
    public static RpcException PermissionDenied(string? message = null)
        => new(new Status(StatusCode.PermissionDenied, message ?? "Permission denied."));
}
