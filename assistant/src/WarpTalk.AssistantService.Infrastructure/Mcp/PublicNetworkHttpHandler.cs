using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// Applies <see cref="PublicNetworkHttpHandler"/> to the typed clients that fetch third-party URLs.
/// </summary>
public static class McpEgressHttpClientBuilderExtensions
{
    /// <summary>
    /// Refuses connections to anything but the public internet, outside Development.
    /// </summary>
    /// <remarks>
    /// Development is exempt for the same reason <c>RequirePublicBaseUrl</c> exempts it: a
    /// developer running an MCP server does so on localhost, and the guard would refuse it with a
    /// connection error whose cause is not obvious from the message. Every other environment gets
    /// it, so nothing has to be switched on for production to be protected - which is the direction
    /// this kind of setting has to fail in.
    /// </remarks>
    public static IHttpClientBuilder ConfigureMcpEgress(
        this IHttpClientBuilder builder,
        IHostEnvironment environment) =>
        environment.IsDevelopment()
            ? builder
            : builder.ConfigurePrimaryHttpMessageHandler(PublicNetworkHttpHandler.Create);
}

/// <summary>
/// An HTTP handler that refuses to open a connection to anything but a public internet address.
/// </summary>
/// <remarks>
/// Every URL an MCP plugin causes us to fetch comes from outside this service. A system admin types
/// <c>mcp_server_url</c>; that server's protected-resource document names its
/// <c>authorization_servers</c>; its <c>WWW-Authenticate</c> header names a
/// <c>resource_metadata</c> URL; the authorization server's own metadata names a
/// <c>registration_endpoint</c> we then POST to. Only the first has a human in the loop, and after
/// one row exists the remote server steers the rest on its own.
/// <para>
/// Guarding at connect time rather than by validating the strings has two advantages. It covers
/// every one of those hops at once, including any added later, instead of four checks that have to
/// be remembered separately. And it closes DNS rebinding: a hostname that resolves public on a
/// validation pass and private a moment later is checked here against the very address the socket
/// is about to use, because this callback resolves the name and then connects to the address it
/// vetted rather than handing the name back to the stack.
/// </para>
/// <para>
/// The failure is a refused connection, which the callers already treat as an unreachable provider:
/// discovery falls through to its next candidate, and the ladder reports
/// <c>provider_unavailable</c>. An operator who has genuinely put an MCP server on a private network
/// gets a connection error naming the address, not a silent empty result.
/// </para>
/// </remarks>
public static class PublicNetworkHttpHandler
{
    /// <summary>
    /// A primary handler for any typed client that fetches a URL this service did not author.
    /// </summary>
    public static SocketsHttpHandler Create() =>
        new()
        {
            // Redirects are followed by HttpClient without re-entering any string validation, so a
            // 302 to http://169.254.169.254 would otherwise be a way straight past a URL check.
            // It is not past this one - every hop opens a connection through the callback below.
            ConnectCallback = ConnectToPublicAddressAsync,
        };

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        var permitted = addresses.Where(IsPublic).ToArray();

        if (permitted.Length == 0)
        {
            // Named rather than generic: an operator who pointed a catalog row at an internal
            // service needs to see which address was refused, and a reader of the logs needs to be
            // able to tell this apart from the host simply being down.
            throw new HttpRequestException(
                $"Refusing to connect to '{host}': it resolves only to addresses outside the public "
                    + "internet, and an MCP server URL may not reach into this network.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // The vetted addresses, never the hostname - re-resolving here is exactly the window
            // this class exists to close.
            await socket.ConnectAsync(permitted, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether an address is one the open internet can route to.
    /// </summary>
    /// <remarks>
    /// Written as a deny list of the ranges that reach somewhere private, because the alternative -
    /// an allow list of public space - is the whole address space minus these, and would have to be
    /// rewritten every time IANA assigns a block.
    /// <para>
    /// Deliberately public so it can be tested directly. The connect callback around it is not
    /// reachable from a unit test without a live socket.
    /// </para>
    /// </remarks>
    public static bool IsPublic(IPAddress address)
    {
        // An IPv4 address arriving in IPv6 clothing (::ffff:169.254.169.254) is an IPv4 address for
        // every purpose that matters here, and checking it as IPv6 would let it through.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            // Anything else cannot be a public internet host: refuse rather than guess.
            _ => false,
        };
    }

    private static bool IsPublicIPv4(byte[] octets)
    {
        var (a, b) = (octets[0], octets[1]);

        return a switch
        {
            0 => false,                                   // "this network"
            10 => false,                                  // RFC 1918
            127 => false,                                 // loopback, already caught but explicit
            100 when b is >= 64 and <= 127 => false,       // RFC 6598 carrier-grade NAT
            169 when b == 254 => false,                    // link-local - the cloud metadata endpoint
            172 when b is >= 16 and <= 31 => false,         // RFC 1918
            192 when b == 168 => false,                    // RFC 1918
            192 when b == 0 => false,                       // IETF protocol assignments
            198 when b is 18 or 19 => false,                // RFC 2544 benchmarking
            >= 224 => false,                               // multicast and reserved
            _ => true,
        };
    }

    private static bool IsPublicIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;

        var bytes = address.GetAddressBytes();

        // fc00::/7 - unique local addresses, IPv6's answer to RFC 1918. IsIPv6SiteLocal covers only
        // the deprecated fec0::/10, so this is not redundant with the check above.
        if ((bytes[0] & 0xFE) == 0xFC) return false;

        // 64:ff9b::/96 - NAT64. What it translates to is an IPv4 address we never get to inspect,
        // so the prefix itself has to be refused.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B) return false;

        return true;
    }
}
