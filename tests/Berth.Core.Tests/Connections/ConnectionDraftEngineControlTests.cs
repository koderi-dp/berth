using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class ConnectionDraftEngineControlTests
{
    private static readonly ConnectionDraft Valid = new("Local", "tcp://127.0.0.1:2375", false, "", "", "");

    private const string Start = "wsl.exe -d Ubuntu-24.04 -u root -- systemctl start docker";
    private const string Stop = "wsl.exe -d Ubuntu-24.04 -u root -- systemctl stop docker.socket docker";

    [Fact]
    public void Engine_control_is_off_in_a_new_draft()
    {
        Assert.False(ConnectionDraft.Empty.EngineControlOn);
        Assert.Equal("", ConnectionDraft.Empty.StartCommand);
        Assert.Equal("", ConnectionDraft.Empty.StopCommand);
    }

    [Fact]
    public void Off_builds_a_profile_without_engine_control_even_with_commands_typed()
    {
        var draft = Valid with { StartCommand = Start, StopCommand = "\"open" };

        Assert.True(draft.TryBuild("id", out var profile, out _));
        Assert.Null(profile.EngineControl);
    }

    [Fact]
    public void On_with_both_commands_builds_a_profile_with_them_trimmed()
    {
        var draft = Valid with { EngineControlOn = true, StartCommand = "  " + Start + " ", StopCommand = Stop };

        Assert.True(draft.TryBuild("id", out var profile, out _));
        Assert.Equal(new EngineControl(Start, Stop), profile.EngineControl);
    }

    [Fact]
    public void On_needs_both_commands()
    {
        var neither = Valid with { EngineControlOn = true };
        Assert.False(neither.TryBuild("id", out _, out var error));
        Assert.Equal(ConnectionDraft.StartCommandMissing, error);
        Assert.Equal(ConnectionDraft.StartCommandMissing, neither.StartCommandError);
        Assert.Equal(ConnectionDraft.StopCommandMissing, neither.StopCommandError);

        var startOnly = Valid with { EngineControlOn = true, StartCommand = Start };
        Assert.False(startOnly.TryBuild("id", out _, out error));
        Assert.Equal(ConnectionDraft.StopCommandMissing, error);
        Assert.Null(startOnly.StartCommandError);
    }

    [Fact]
    public void Each_command_must_parse()
    {
        var draft = Valid with { EngineControlOn = true, StartCommand = Start, StopCommand = "wsl.exe \"open" };

        Assert.False(draft.TryBuild("id", out _, out var error));
        Assert.Equal("Stop command: " + CommandLine.OpenQuote, error);
        Assert.Equal(error, draft.StopCommandError);
    }

    [Fact]
    public void The_name_and_address_are_checked_before_the_commands()
    {
        var draft = Valid with { Name = "", EngineControlOn = true };

        Assert.False(draft.TryBuild("id", out _, out var error));
        Assert.Equal(ConnectionDraft.NameMissing, error);
    }

    [Fact]
    public void From_fills_the_engine_control_fields()
    {
        var profile = new ConnectionProfile("id", "Local", TransportKind.Tcp, "127.0.0.1", 2375, "", null) { EngineControl = new EngineControl(Start, Stop) };

        var draft = ConnectionDraft.From(profile);

        Assert.True(draft.EngineControlOn);
        Assert.Equal(Start, draft.StartCommand);
        Assert.Equal(Stop, draft.StopCommand);
        Assert.True(draft.TryBuild("id", out var built, out _));
        Assert.Equal(profile, built);
    }

    [Fact]
    public void With_engine_control_default_fills_both_commands_and_turns_it_on()
    {
        var draft = Valid.WithEngineControl(EngineControl.WslDefault("Ubuntu-24.04"));

        Assert.True(draft.EngineControlOn);
        Assert.Equal(Start, draft.StartCommand);
        Assert.Equal(Stop, draft.StopCommand);
    }

    [Fact]
    public void Profiles_with_different_commands_are_not_equal()
    {
        var a = new ConnectionProfile("id", "Local", TransportKind.Tcp, "127.0.0.1", 2375, "", null) { EngineControl = new EngineControl(Start, Stop) };

        Assert.NotEqual(a, a with { EngineControl = new EngineControl(Start, "other") });
        Assert.NotEqual(a, a with { EngineControl = null });
    }
}
