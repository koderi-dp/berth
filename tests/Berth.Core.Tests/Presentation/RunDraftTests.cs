using Berth.Engine.Models;
using Berth.Presentation;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

public sealed class RunDraftTests
{
    private static readonly RunDraft Nginx = RunDraft.For("nginx:latest", ["80/tcp", "443/tcp", "53/udp"]);

    [Fact]
    public void A_draft_has_one_host_port_field_per_exposed_port_and_starts_valid()
    {
        Assert.Equal<PortField>(
            [new PortField(80, "tcp", ""), new PortField(443, "tcp", ""), new PortField(53, "udp", "")],
            Nginx.Ports);
        Assert.Equal("nginx:latest", Nginx.Image);
        Assert.Empty(Nginx.Volumes);
        Assert.Empty(Nginx.Env);
        Assert.True(Nginx.Validate().IsValid);
    }

    [Fact]
    public void Exposed_ports_without_a_protocol_are_tcp_and_unreadable_ones_are_left_out()
    {
        var draft = RunDraft.For("x", ["8080", "nonsense/tcp", "70000/tcp"]);

        Assert.Equal<PortField>([new PortField(8080, "tcp", "")], draft.Ports);
    }

    [Theory]
    [InlineData("")]
    [InlineData("web")]
    [InlineData("web-1.blue_2")]
    [InlineData("9lives")]
    public void A_name_may_be_blank_or_letters_digits_and_underscore_dot_dash(string name)
    {
        Assert.Null((Nginx with { Name = name }).Validate().Name);
    }

    [Theory]
    [InlineData("my web")]
    [InlineData("-web")]
    [InlineData(".web")]
    [InlineData("wéb")]
    [InlineData("web/1")]
    public void Any_other_name_is_refused(string name)
    {
        var errors = (Nginx with { Name = name }).Validate();

        Assert.Equal("Use letters, digits, _, . and -, starting with a letter or digit.", errors.Name);
        Assert.False(errors.IsValid);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("1", null)]
    [InlineData(" 8080 ", null)]
    [InlineData("65535", null)]
    [InlineData("65536", "Enter a port from 1 to 65535, or 0 for any free port.")]
    [InlineData("-1", "Enter a port from 1 to 65535, or 0 for any free port.")]
    [InlineData("80a", "Enter a port from 1 to 65535, or 0 for any free port.")]
    public void A_host_port_is_blank_0_or_1_to_65535(string port, string? error)
    {
        var errors = Nginx.WithHostPort(0, port).Validate();

        Assert.Equal(error, errors.Ports[0]);
        Assert.Equal(error is null, errors.IsValid);
    }

    [Fact]
    public void The_same_host_port_twice_is_refused_on_the_second_field()
    {
        var errors = Nginx.WithHostPort(0, "8080").WithHostPort(1, "8080").Validate();

        Assert.Null(errors.Ports[0]);
        Assert.Equal("Port 8080 is used by another field.", errors.Ports[1]);
        Assert.Null(Nginx.WithHostPort(0, "0").WithHostPort(1, "0").Validate().Ports[1]);
    }

    [Fact]
    public void A_host_port_another_container_publishes_is_refused()
    {
        var published = RunDraft.PublishedPorts(
        [
            FakeContainer("web", "running", new PortMapping("0.0.0.0", 80, 8080, "tcp")),
            FakeContainer("old", "exited", new PortMapping(null, 80, 9090, "tcp")),
        ]);

        Assert.Equal("Port 8080 is already published by another container.", Nginx.WithHostPort(0, "8080").Validate(published).Ports[0]);
        Assert.Null(Nginx.WithHostPort(0, "9090").Validate(published).Ports[0]);
    }

    [Theory]
    [InlineData("", "", null)]
    [InlineData("C:\\data", "/data", null)]
    [InlineData("/mnt/c/data", "/data", null)]
    [InlineData("data-volume", "/data", null)]
    [InlineData("C:\\data", "", "Enter both paths, or neither.")]
    [InlineData("", "/data", "Enter both paths, or neither.")]
    [InlineData("C:\\data", "data", "The container path must start with /.")]
    public void A_volume_row_needs_both_paths_or_neither_and_an_absolute_container_path(string host, string container, string? error)
    {
        var errors = Nginx.AddVolume().WithVolume(0, new VolumeField(host, container)).Validate();

        Assert.Equal(error, errors.Volumes[0]);
    }

    [Theory]
    [InlineData("", "", null)]
    [InlineData("MODE", "", null)]
    [InlineData("MODE", "a=b", null)]
    [InlineData("", "x", "Enter the variable's name.")]
    [InlineData("A=B", "x", "A name cannot contain =.")]
    public void An_environment_row_needs_a_name_without_an_equals_sign_when_it_has_a_value(string name, string value, string? error)
    {
        var errors = Nginx.AddEnv().WithEnv(0, new EnvField(name, value)).Validate();

        Assert.Equal(error, errors.Env[0]);
    }

    [Fact]
    public void A_variable_set_twice_is_refused_on_the_second_row()
    {
        var draft = Nginx.AddEnv().AddEnv().AddEnv()
            .WithEnv(0, new EnvField("MODE", "a"))
            .WithEnv(1, new EnvField("OTHER", "b"))
            .WithEnv(2, new EnvField("MODE", "c"));

        var errors = draft.Validate();

        Assert.Equal<string?>([null, null, "MODE is set more than once."], errors.Env);
    }

    [Fact]
    public void Rows_are_added_and_removed()
    {
        var draft = Nginx.AddVolume().AddVolume().WithVolume(1, new VolumeField("/a", "/b")).RemoveVolume(0).AddEnv().RemoveEnv(0);

        Assert.Equal<VolumeField>([new VolumeField("/a", "/b")], draft.Volumes);
        Assert.Empty(draft.Env);
    }

    [Fact]
    public void The_request_carries_what_was_filled_in_and_leaves_out_blank_rows()
    {
        var draft = (Nginx with { Name = " web " })
            .WithHostPort(0, "8080")
            .WithHostPort(2, "0")
            .AddVolume().WithVolume(0, new VolumeField("C:\\site", "/usr/share/nginx/html"))
            .AddVolume()
            .AddEnv().WithEnv(0, new EnvField("MODE", "prod=1"))
            .AddEnv().WithEnv(1, new EnvField("EMPTY", ""))
            .AddEnv();

        var request = draft.ToRequest();

        Assert.Equal("nginx:latest", request.Image);
        Assert.Equal("web", request.Name);
        Assert.Equal<PortBinding>([new PortBinding(80, "tcp", null, 8080), new PortBinding(53, "udp", null, null)], request.Ports);
        Assert.Equal(["C:\\site:/usr/share/nginx/html"], request.Binds);
        Assert.Equal<string>(["MODE=prod=1", "EMPTY="], request.Env);
        Assert.Null(request.Cmd);
    }

    [Fact]
    public void A_blank_name_lets_the_engine_name_the_container()
    {
        Assert.Null(Nginx.ToRequest().Name);
        Assert.Empty(Nginx.ToRequest().Ports);
    }

    [Fact]
    public void An_invalid_draft_makes_no_request()
    {
        Assert.Throws<InvalidOperationException>(() => (Nginx with { Name = "my web" }).ToRequest());
    }

    [Fact]
    public void The_host_path_note_tells_a_WSL_engine_wants_mnt_paths()
    {
        Assert.Contains("/mnt/c/", RunDraft.HostPathNote, StringComparison.Ordinal);
    }

    private static ContainerSummary FakeContainer(string id, string state, PortMapping port) =>
        User(id, state: state) with { Ports = [port] };
}
