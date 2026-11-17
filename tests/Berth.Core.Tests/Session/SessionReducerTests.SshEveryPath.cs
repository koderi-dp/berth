using Berth.Connections;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Session;

public sealed partial class SessionReducerTests
{
    private static readonly SshProblem RefusedPassword =
        new(SshFailure.SignInRefused, "Permission denied (password).") { Secret = SecretKind.Password };

    /// <summary>
    /// Every failure that can come from opening a stream on the transport, carrying a refused sign-in, in a state where the reducer
    /// takes it: each must stop and ask, never schedule a retry (a retry would send the refused secret again).
    /// </summary>
    public static TheoryData<string> FailurePaths =>
        ["action", "run command", "resource load", "disk usage", "image inspect", "run", "volume create", "load"];

    private static (EngineState State, SessionEvent Failure) FailureOn(string path)
    {
        var connected = Connected();
        var message = RefusedPassword.Message;
        return path switch
        {
            "action" => (connected, new ActionFailed("a", message, EngineUnreachable: true) { Ssh = RefusedPassword }),
            "run command" => (connected, new RunCommandFailed("a", message, EngineUnreachable: true) { Ssh = RefusedPassword }),
            "resource load" => (connected, new ResourceLoadFailed(ResourceKind.Images, connected.Images.Status.Generation, message, EngineUnreachable: true) { Ssh = RefusedPassword }),
            "disk usage" => (
                connected with { DiskUsage = connected.DiskUsage with { Loading = true, Generation = 3 } },
                new DiskUsageFailed(3, message, EngineUnreachable: true) { Ssh = RefusedPassword }),
            "image inspect" => (
                connected with { ImageInspect = new ImageInspect("img", 4, null, null) },
                new ImageInspectFailed(4, message, EngineUnreachable: true) { Ssh = RefusedPassword }),
            "run" => (
                connected with { Run = new ContainerRun(5, "alpine", RunStatus.InProgress, null, null) },
                new RunFailed(5, null, message, EngineUnreachable: true) { Ssh = RefusedPassword }),
            "volume create" => (
                Reduce(connected, new CreateVolumeRequested(1, "v")).State,
                new VolumeCreateFailed("v", message, EngineUnreachable: true) { Ssh = RefusedPassword }),
            _ => (connected, new LoadFailed(connected.LoadGeneration, message, EngineUnreachable: true) { Ssh = RefusedPassword }),
        };
    }

    [Theory]
    [MemberData(nameof(FailurePaths))]
    public void A_refused_sign_in_from_any_failure_path_stops_and_asks_without_a_retry(string path)
    {
        var (state, failure) = FailureOn(path);

        var (next, effects) = Reduce(state, failure);

        Assert.Equal(ConnectionStatus.Disconnected, next.Status);
        Assert.Same(RefusedPassword, next.Attention);
        Assert.DoesNotContain(effects, effect => effect is ScheduleRetryEffect);
    }
}
