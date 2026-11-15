using Berth.Engine.Models;
using Berth.Presentation;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.Presentation;

public sealed class InspectSectionsTests
{
    [Fact]
    public void Container_with_nothing_optional_has_only_state_image_and_runtime()
    {
        var sections = InspectSections.Build(Details());

        Assert.Equal(["State", "Image", "Runtime"], sections.Select(s => s.Title));
    }

    [Fact]
    public void Sections_come_in_a_fixed_order()
    {
        var details = Details() with
        {
            Platform = "linux",
            Cmd = ["nginx"],
            Ports = [new PortMapping(null, 80, null, "tcp")],
            Mounts = [new MountDetails("bind", "/srv", "/data", "", true, null)],
            Env = ["A=1"],
            Labels = new Dictionary<string, string> { ["a"] = "b" },
            Networks = [new NetworkAttachment("bridge", "172.17.0.2", "172.17.0.1", "02:42:ac:11:00:02")],
        };

        var sections = InspectSections.Build(details);

        Assert.Equal(
            ["Platform", "Cmd", "State", "Image", "Ports", "Runtime", "Mounts", "Env", "Labels", "Networks"],
            sections.Select(s => s.Title));
    }

    [Fact]
    public void Platform_section_names_the_platform()
    {
        var section = Section(Details() with { Platform = "linux" }, "Platform");

        Assert.Equal([new InspectRow("Platform", "linux")], section.Rows);
    }

    [Fact]
    public void Cmd_section_has_the_entrypoint_and_command_each_joined_with_spaces()
    {
        var section = Section(
            Details() with { Entrypoint = ["/docker-entrypoint.sh"], Cmd = ["nginx", "-g", "daemon off;"] },
            "Cmd");

        Assert.Equal(
            [new InspectRow("Entrypoint", "/docker-entrypoint.sh"), new InspectRow("Cmd", "nginx -g daemon off;")],
            section.Rows);
    }

    [Fact]
    public void Cmd_section_leaves_out_a_missing_entrypoint()
    {
        var section = Section(Details() with { Cmd = ["sh"] }, "Cmd");

        Assert.Equal([new InspectRow("Cmd", "sh")], section.Rows);
    }

    [Fact]
    public void State_section_of_a_running_container()
    {
        var section = Section(Details(), "State");

        Assert.Equal(
            [
                new InspectRow("Status", "running"),
                new InspectRow("Running", "Yes"),
                new InspectRow("Paused", "No"),
                new InspectRow("Restarting", "No"),
                new InspectRow("Exit code", "0"),
                new InspectRow("Started", "2026-11-16 10:00:00 UTC"),
            ],
            section.Rows);
    }

    [Fact]
    public void State_section_of_a_failed_container_has_its_error_finish_time_and_health()
    {
        var local = new DateTimeOffset(2026, 11, 16, 12, 30, 5, TimeSpan.FromHours(1));
        var details = Details() with
        {
            State = new ContainerStateDetails("exited", false, false, false, 137, "OOM", T0, local, "unhealthy"),
        };

        var section = Section(details, "State");

        Assert.Equal(
            [
                new InspectRow("Status", "exited"),
                new InspectRow("Running", "No"),
                new InspectRow("Paused", "No"),
                new InspectRow("Restarting", "No"),
                new InspectRow("Exit code", "137"),
                new InspectRow("Error", "OOM"),
                new InspectRow("Started", "2026-11-16 10:00:00 UTC"),
                new InspectRow("Finished", "2026-11-16 11:30:05 UTC"),
                new InspectRow("Health", "unhealthy"),
            ],
            section.Rows);
    }

    [Fact]
    public void State_section_of_a_container_that_never_started_has_no_times()
    {
        var details = Details() with
        {
            State = new ContainerStateDetails("created", false, false, false, 0, "", null, null, null),
        };

        Assert.DoesNotContain(Section(details, "State").Rows, r => r.Name is "Started" or "Finished");
    }

    [Fact]
    public void Image_section_has_the_name_and_id()
    {
        var section = Section(Details(), "Image");

        Assert.Equal([new InspectRow("Image", "nginx:alpine"), new InspectRow("Image ID", "sha256:1111")], section.Rows);
    }

    [Fact]
    public void Ports_section_has_one_row_per_container_port_with_its_bindings()
    {
        var details = Details() with
        {
            Ports =
            [
                new PortMapping("0.0.0.0", 80, 8080, "tcp"),
                new PortMapping(null, 443, null, "tcp"),
                new PortMapping("::", 80, 8080, "tcp"),
                new PortMapping("", 53, 5353, "udp"),
            ],
        };

        var section = Section(details, "Ports");

        Assert.Equal(
            [
                new InspectRow("53/udp", "5353"),
                new InspectRow("80/tcp", "0.0.0.0:8080, [::]:8080"),
                new InspectRow("443/tcp", "Not published"),
            ],
            section.Rows);
    }

    [Fact]
    public void Runtime_section_has_created_working_directory_user_tty_and_restart_policy()
    {
        var details = Details() with { WorkingDir = "/app", User = "1000", Tty = true, RestartPolicy = "always" };

        var section = Section(details, "Runtime");

        Assert.Equal(
            [
                new InspectRow("Created", "2026-11-16 10:00:00 UTC"),
                new InspectRow("Working directory", "/app"),
                new InspectRow("User", "1000"),
                new InspectRow("TTY", "Yes"),
                new InspectRow("Restart policy", "always"),
            ],
            section.Rows);
    }

    [Fact]
    public void Runtime_section_leaves_out_an_empty_working_directory_user_and_restart_policy()
    {
        var section = Section(Details() with { RestartPolicy = "" }, "Runtime");

        Assert.Equal([new InspectRow("Created", "2026-11-16 10:00:00 UTC"), new InspectRow("TTY", "No")], section.Rows);
    }

    [Fact]
    public void Mounts_section_names_each_destination_with_its_source_type_and_access()
    {
        var details = Details() with
        {
            Mounts =
            [
                new MountDetails("bind", "/srv/data", "/data", "", true, null),
                new MountDetails("volume", "/var/lib/docker/volumes/cache/_data", "/cache", "z", false, "cache"),
            ],
        };

        var section = Section(details, "Mounts");

        Assert.Equal(
            [
                new InspectRow("/data", "/srv/data (bind, read-write)"),
                new InspectRow("/cache", "cache (volume, read-only)"),
            ],
            section.Rows);
    }

    [Fact]
    public void Env_section_splits_each_entry_at_its_first_equals_sign()
    {
        var section = Section(Details() with { Env = ["PATH=/usr/bin", "OPTS=a=b", "EMPTY=", "BARE"] }, "Env");

        Assert.Equal(
            [
                new InspectRow("PATH", "/usr/bin"),
                new InspectRow("OPTS", "a=b"),
                new InspectRow("EMPTY", ""),
                new InspectRow("BARE", ""),
            ],
            section.Rows);
    }

    [Fact]
    public void Labels_section_is_ordered_by_key()
    {
        var details = Details() with
        {
            Labels = new Dictionary<string, string> { ["maintainer"] = "me", ["com.example.b"] = "2", ["com.example.a"] = "1" },
        };

        var section = Section(details, "Labels");

        Assert.Equal(
            [new InspectRow("com.example.a", "1"), new InspectRow("com.example.b", "2"), new InspectRow("maintainer", "me")],
            section.Rows);
    }

    [Fact]
    public void Networks_section_gives_each_network_s_address()
    {
        var details = Details() with
        {
            Networks =
            [
                new NetworkAttachment("web_default", "172.18.0.3", "172.18.0.1", ""),
                new NetworkAttachment("bridge", "", "", ""),
            ],
        };

        var section = Section(details, "Networks");

        Assert.Equal([new InspectRow("bridge", "No address"), new InspectRow("web_default", "172.18.0.3")], section.Rows);
    }

    [Fact]
    public void Bind_mounts_are_the_mounts_of_type_bind()
    {
        var bind = new MountDetails("bind", "/srv/data", "/data", "", true, null);
        var otherBind = new MountDetails("bind", "/etc/app.conf", "/app.conf", "ro", false, null);
        var details = Details() with
        {
            Mounts =
            [
                bind,
                new MountDetails("volume", "/var/lib/docker/volumes/v/_data", "/v", "", true, "v"),
                new MountDetails("tmpfs", "", "/tmp", "", true, null),
                otherBind,
            ],
        };

        Assert.Equal([bind, otherBind], InspectSections.BindMounts(details));
    }

    [Fact]
    public void Container_without_bind_mounts_has_none()
    {
        Assert.Empty(InspectSections.BindMounts(Details()));
    }

    private static InspectSection Section(ContainerDetails details, string title) =>
        Assert.Single(InspectSections.Build(details), s => s.Title == title);
}
