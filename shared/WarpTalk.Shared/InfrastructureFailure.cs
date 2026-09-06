using System;
using System.Data.Common;
using System.IO;
using System.Net.Sockets;

namespace WarpTalk.Shared;

/// <summary>
/// WT-596: tells "we could not reach a dependency" apart from "the request was wrong".
///
/// A catch-all around a service method sees both, and collapsing them into one error code is how
/// a Postgres outage reached the browser as <c>400 Bad Request</c> — 4xx is read by humans as
/// "you typed it wrong" and by alerting as a client error that needs no page, so a full outage
/// can pass in silence while everyone inspects the validator.
///
/// Classification is by exception SHAPE, not by referencing a driver: <see cref="DbException"/>
/// is the BCL base every ADO.NET provider derives from (Npgsql included), and Redis is matched by
/// namespace so the Application layer keeps no dependency on the client library.
///
/// The inner chain is walked, because the interesting exception is almost never the outermost
/// one: EF wraps a Npgsql socket failure in <c>InvalidOperationException</c>, and MassTransit and
/// the gRPC clients wrap theirs the same way.
/// </summary>
public static class InfrastructureFailure
{
    /// <summary>How deep the inner-exception chain is walked. Deep enough for EF-over-Npgsql
    /// wrapping, bounded so a cyclic chain cannot hang the catch block it runs in.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// True when <paramref name="exception"/> means a dependency could not be reached or did not
    /// answer — database, cache, broker, or the network under any of them.
    /// </summary>
    public static bool IsDependencyUnreachable(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (MaxDepthReached(exception, current))
            {
                return false;
            }

            if (current is DbException          // every ADO.NET provider, Npgsql included
                or SocketException              // connection refused / host unreachable
                or TimeoutException
                or IOException                  // a connection dropped mid-read
                or OperationCanceledException)  // a command that ran past its timeout
            {
                return true;
            }

            // StackExchange.Redis exceptions derive from its own base, not from a BCL one.
            // Matched by namespace so this file needs no reference to the client.
            var typeNamespace = current.GetType().Namespace;
            if (typeNamespace is not null && typeNamespace.StartsWith("StackExchange.Redis", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MaxDepthReached(Exception? root, Exception current)
    {
        var depth = 0;
        for (var walk = root; walk is not null && !ReferenceEquals(walk, current); walk = walk.InnerException)
        {
            depth++;
        }

        return depth >= MaxDepth;
    }

    /// <summary>
    /// The error code to report for <paramref name="exception"/>: <see cref="ErrorCodes.ServiceUnavailable"/>
    /// when a dependency is down, <see cref="ErrorCodes.InternalServerError"/> when the fault is ours.
    /// </summary>
    public static string ClassifyErrorCode(Exception? exception)
        => IsDependencyUnreachable(exception)
            ? ErrorCodes.ServiceUnavailable
            : ErrorCodes.InternalServerError;
}
