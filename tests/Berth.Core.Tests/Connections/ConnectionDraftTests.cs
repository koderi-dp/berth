using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class ConnectionDraftTests
{
    private static readonly ConnectionDraft Valid = new("Build host", "tcp://build-host:2375", false, "", "", "");

    [Fact]
    public void Empty_is_a_local_tcp_address_without_tls()
    {
        var draft = ConnectionDraft.Empty;

        Assert.Equal("", draft.Name);
        Assert.Equal("tcp://127.0.0.1:2375", draft.Address);
        Assert.False(draft.UseTls);
        Assert.Equal("", draft.CaPath);
        Assert.Equal("", draft.CertPath);
        Assert.Equal("", draft.KeyPath);
    }

    [Fact]
    public void From_fills_the_fields_of_a_tls_profile()
    {
        var profile = new ConnectionProfile("id", "Build host", TransportKind.Tcp, "build-host", 2376, "", new TlsOptions(@"C:\ca.pem", @"C:\cert.pem", @"C:\key.pem"));

        var draft = ConnectionDraft.From(profile);

        Assert.Equal(new ConnectionDraft("Build host", "tcp://build-host:2376", true, @"C:\ca.pem", @"C:\cert.pem", @"C:\key.pem"), draft);
    }

    [Fact]
    public void Turning_tls_on_moves_the_default_port_to_the_tls_port()
    {
        var draft = Valid.WithTls(true);

        Assert.True(draft.UseTls);
        Assert.Equal("tcp://build-host:2376", draft.Address);
    }

    [Fact]
    public void Turning_tls_on_gives_an_address_without_a_port_the_tls_port()
    {
        var draft = (Valid with { Address = "tcp://build-host" }).WithTls(true);

        Assert.Equal("tcp://build-host:2376", draft.Address);
    }

    [Fact]
    public void Turning_tls_off_moves_the_tls_port_back_to_the_default_port()
    {
        var draft = (Valid with { Address = "tcp://build-host:2376", UseTls = true }).WithTls(false);

        Assert.False(draft.UseTls);
        Assert.Equal("tcp://build-host:2375", draft.Address);
    }

    [Fact]
    public void Turning_tls_on_keeps_a_port_that_is_not_the_default()
    {
        var draft = (Valid with { Address = "tcp://build-host:9000" }).WithTls(true);

        Assert.True(draft.UseTls);
        Assert.Equal("tcp://build-host:9000", draft.Address);
    }

    [Fact]
    public void Turning_tls_off_keeps_a_port_that_is_not_the_tls_port()
    {
        var draft = (Valid with { Address = "tcp://build-host:9000", UseTls = true }).WithTls(false);

        Assert.Equal("tcp://build-host:9000", draft.Address);
    }

    [Fact]
    public void Turning_tls_on_leaves_an_address_that_does_not_parse_alone()
    {
        var draft = (Valid with { Address = "http://x:2375" }).WithTls(true);

        Assert.True(draft.UseTls);
        Assert.Equal("http://x:2375", draft.Address);
    }

    [Fact]
    public void Turning_tls_on_leaves_a_pipe_address_alone()
    {
        var draft = (Valid with { Address = "npipe:////./pipe/docker_engine" }).WithTls(true);

        Assert.Equal("npipe:////./pipe/docker_engine", draft.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused(string name)
    {
        Assert.False((Valid with { Name = name }).TryBuild("id", out _, out var error));
        Assert.Equal("Enter a name.", error);
    }

    [Fact]
    public void The_name_is_trimmed()
    {
        Assert.True((Valid with { Name = "  Build host  " }).TryBuild("id", out var profile, out _));
        Assert.Equal("Build host", profile.Name);
    }

    [Fact]
    public void An_address_that_does_not_parse_is_refused_with_its_parse_error()
    {
        Assert.False((Valid with { Address = "http://x" }).TryBuild("id", out _, out var error));
        Assert.Equal("Use tcp://, npipe:// or ssh://.", error);
    }

    [Fact]
    public void Tls_without_a_ca_path_is_refused()
    {
        var draft = Valid with { Address = "tcp://build-host:2376", UseTls = true, CaPath = "  " };

        Assert.False(draft.TryBuild("id", out _, out var error));
        Assert.Equal("Choose the CA certificate file.", error);
    }

    [Fact]
    public void Tls_is_ignored_for_a_named_pipe()
    {
        var draft = Valid with { Address = "npipe:////./pipe/docker_engine", UseTls = true, CaPath = "" };

        Assert.True(draft.TryBuild("id", out var profile, out _));
        Assert.Equal(TransportKind.NamedPipe, profile.Kind);
        Assert.Null(profile.Tls);
    }

    [Fact]
    public void Blank_client_certificate_and_key_become_null()
    {
        var draft = Valid with { Address = "tcp://build-host:2376", UseTls = true, CaPath = @"C:\ca.pem", CertPath = " ", KeyPath = "" };

        Assert.True(draft.TryBuild("id", out var profile, out _));
        Assert.Equal(new TlsOptions(@"C:\ca.pem", null, null), profile.Tls);
    }

    [Theory]
    [InlineData(@"C:\cert.pem", "")]
    [InlineData("", @"C:\key.pem")]
    public void Only_one_of_client_certificate_and_key_is_refused(string cert, string key)
    {
        var draft = Valid with { Address = "tcp://build-host:2376", UseTls = true, CaPath = @"C:\ca.pem", CertPath = cert, KeyPath = key };

        Assert.False(draft.TryBuild("id", out _, out var error));
        Assert.Equal("Set both the client certificate and the client key, or neither.", error);
    }

    [Fact]
    public void Tls_off_builds_a_profile_without_tls_even_with_paths_filled_in()
    {
        var draft = Valid with { CaPath = @"C:\ca.pem", CertPath = @"C:\cert.pem" };

        Assert.True(draft.TryBuild("id", out var profile, out var error));
        Assert.Equal("", error);
        Assert.Equal(new ConnectionProfile("id", "Build host", TransportKind.Tcp, "build-host", 2375, "", null), profile);
    }

    [Fact]
    public void Address_error_is_the_parse_error_or_null()
    {
        Assert.Equal("Use tcp://, npipe:// or ssh://.", (Valid with { Address = "http://x" }).AddressError);
        Assert.Equal("Enter an address.", (Valid with { Address = "" }).AddressError);
        Assert.Null(Valid.AddressError);
    }

    [Theory]
    [InlineData("npipe:////./pipe/docker_engine", true)]
    [InlineData("tcp://build-host:2375", false)]
    [InlineData("http://x", false)]
    public void Is_named_pipe_is_true_only_for_a_pipe_address_that_parses(string address, bool expected)
    {
        Assert.Equal(expected, (Valid with { Address = address }).IsNamedPipe);
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("tls")]
    [InlineData("pipe")]
    public void A_profile_round_trips_through_a_draft(string kind)
    {
        var profile = kind switch
        {
            "tcp" => new ConnectionProfile("tcp", "Local", TransportKind.Tcp, "127.0.0.1", 2375, "", null),
            "tls" => new ConnectionProfile("tls", "Build host", TransportKind.Tcp, "build-host", 2376, "", new TlsOptions(@"C:\ca.pem", @"C:\cert.pem", @"C:\key.pem")),
            _ => new ConnectionProfile("pipe", "Local engine", TransportKind.NamedPipe, ".", 0, "docker_engine", null),
        };

        Assert.True(ConnectionDraft.From(profile).TryBuild(profile.Id, out var built, out var error));
        Assert.Equal("", error);
        Assert.Equal(profile, built);
    }
}
