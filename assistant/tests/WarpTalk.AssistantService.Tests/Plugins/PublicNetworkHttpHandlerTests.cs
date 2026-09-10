using System.Net;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The address table behind the MCP egress guard. WT-646.
/// </summary>
/// <remarks>
/// Tested as a table rather than through a socket because the danger here is a range quietly
/// missing from the deny list, not the plumbing. A gap reads as "the connection worked", which is
/// indistinguishable from correct behaviour until someone points a catalog row at an internal
/// service.
/// </remarks>
public class PublicNetworkHttpHandlerTests
{
    [Theory]
    // The one everybody means by SSRF: EC2/GCP/Azure instance metadata.
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    // RFC 1918, at both edges of each block - an off-by-one in the range test shows up here and
    // nowhere else.
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    // Loopback, including the rest of 127/8 that a naive equality check against 127.0.0.1 misses.
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    // RFC 6598 carrier-grade NAT: routable-looking, reaches the provider's own network.
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6 loopback, unique-local (fc00::/7) and link-local.
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("::")]
    // An IPv4 address wearing IPv6 clothing. Checked as IPv6 this passes every test above.
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    // NAT64: what it translates to is an IPv4 address this code never gets to see.
    [InlineData("64:ff9b::169.254.169.254")]
    public void Refuses(string address) =>
        Assert.False(PublicNetworkHttpHandler.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    // The octets either side of every private block, so the deny list cannot be widened by accident
    // into space real MCP servers live in.
    [InlineData("9.255.255.255")]
    [InlineData("11.0.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.255")]
    // Real public IPv6, including a Cloudflare resolver and a global-unicast prefix next to fc00::/7.
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("fe00::1")]
    public void Permits(string address) =>
        Assert.True(PublicNetworkHttpHandler.IsPublic(IPAddress.Parse(address)));
}
