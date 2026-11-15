using Berth.Engine.Models;
using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class PortFormatTests
{
    [Fact]
    public void Published_port_gives_a_localhost_link()
    {
        var links = PortFormat.Links([new PortMapping("0.0.0.0", 80, 8080, "tcp")]);

        Assert.Equal([new PortLink("8080:80", new Uri("http://localhost:8080"))], links);
    }

    [Theory]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("")]
    [InlineData(null)]
    public void Wildcard_loopback_or_missing_address_links_to_localhost(string? ip)
    {
        var links = PortFormat.Links([new PortMapping(ip, 80, 8080, "tcp")]);

        Assert.Equal([new PortLink("8080:80", new Uri("http://localhost:8080"))], links);
    }

    [Fact]
    public void Port_published_on_a_specific_address_links_to_that_address()
    {
        var links = PortFormat.Links(
        [
            new PortMapping("192.168.1.20", 80, 8080, "tcp"),
            new PortMapping("2001:db8::7", 443, 8443, "tcp"),
        ]);

        Assert.Equal(
            [
                new PortLink("8080:80", new Uri("http://192.168.1.20:8080")),
                new PortLink("8443:443", new Uri("http://[2001:db8::7]:8443")),
            ],
            links);
    }

    [Fact]
    public void Ipv4_and_ipv6_bindings_of_the_same_port_collapse_to_one()
    {
        var links = PortFormat.Links(
        [
            new PortMapping("0.0.0.0", 80, 8080, "tcp"),
            new PortMapping("::", 80, 8080, "tcp"),
        ]);

        Assert.Equal([new PortLink("8080:80", new Uri("http://localhost:8080"))], links);
    }

    [Fact]
    public void Unpublished_port_has_text_and_no_link()
    {
        var links = PortFormat.Links([new PortMapping(null, 80, null, "tcp")]);

        Assert.Equal([new PortLink("80/tcp", null)], links);
    }

    [Fact]
    public void Udp_port_has_no_link()
    {
        var links = PortFormat.Links(
        [
            new PortMapping("0.0.0.0", 53, 5353, "udp"),
            new PortMapping(null, 54, null, "udp"),
        ]);

        Assert.Equal([new PortLink("5353:53/udp", null), new PortLink("54/udp", null)], links);
    }

    [Fact]
    public void Ports_are_ordered_by_public_then_private_port()
    {
        var links = PortFormat.Links(
        [
            new PortMapping(null, 9000, null, "tcp"),
            new PortMapping("0.0.0.0", 443, 9443, "tcp"),
            new PortMapping("0.0.0.0", 81, 8080, "tcp"),
            new PortMapping(null, 22, null, "tcp"),
            new PortMapping("0.0.0.0", 80, 8080, "tcp"),
        ]);

        // Published ports come first, by public port and then private port; unpublished ones follow by private port.
        Assert.Equal(["8080:80", "8080:81", "9443:443", "22/tcp", "9000/tcp"], links.Select(l => l.Text));
    }

    [Fact]
    public void No_ports_gives_no_links()
    {
        Assert.Empty(PortFormat.Links([]));
    }
}
