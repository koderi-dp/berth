using Berth.Connections;
using Berth.Session;

namespace Berth.Tests.Session;

public sealed partial class SessionReducerTests
{
    private static readonly HostKey OfferedKey = new("host", 22, "ssh-ed25519", "SHA256:new");

    public static TheoryData<SshProblem> ProblemsThatNeedTheUser =>
    [
        new SshProblem(SshFailure.HostKeyUnknown, "") { Offered = OfferedKey },
        new SshProblem(SshFailure.HostKeyChanged, "") { Offered = OfferedKey, Saved = OfferedKey with { Fingerprint = "SHA256:old" } },
        new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Password },
        new SshProblem(SshFailure.SignInRefused, "Permission denied (password).") { Secret = SecretKind.Password },
    ];

    private static EngineState Connecting() => Reduce(EngineState.Initial, new ConnectRequested()).State;

    [Theory]
    [MemberData(nameof(ProblemsThatNeedTheUser))]
    public void A_connect_that_needs_the_user_stops_and_asks_instead_of_retrying(SshProblem problem)
    {
        var (next, effects) = Reduce(Connecting(), new ConnectFailed(problem.Message, problem));

        Assert.Equal(ConnectionStatus.Disconnected, next.Status);
        Assert.Same(problem, next.Attention);
        Assert.Equal(problem.Message, next.ConnectionError);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_retry_timer_from_before_does_not_connect_while_the_user_is_asked()
    {
        var problem = new SshProblem(SshFailure.SignInRefused, "") { Secret = SecretKind.Password };
        var (asking, _) = Reduce(Connecting(), new ConnectFailed(problem.Message, problem));

        var (next, effects) = Reduce(asking, new RetryDue());

        Assert.Same(asking, next);
        Assert.Empty(effects);
    }

    [Fact]
    public void An_ssh_failure_that_does_not_need_the_user_retries_as_any_other()
    {
        var problem = new SshProblem(SshFailure.NoDockerCli, "sh: docker: not found");

        var (next, effects) = Reduce(Connecting(), new ConnectFailed(problem.Message, problem));

        Assert.Null(next.Attention);
        Assert.IsType<ScheduleRetryEffect>(Assert.Single(effects));
    }

    [Fact]
    public void Connecting_again_after_the_user_answered_clears_the_question()
    {
        var problem = new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Password };
        var (asking, _) = Reduce(Connecting(), new ConnectFailed(problem.Message, problem));

        var (next, effects) = Reduce(asking, new ConnectRequested());

        Assert.Null(next.Attention);
        Assert.Equal(ConnectionStatus.Connecting, next.Status);
        Assert.Equal([new ConnectEffect()], effects);
    }

    [Fact]
    public void A_lost_connection_whose_reconnect_needs_the_user_does_not_retry()
    {
        var problem = new SshProblem(SshFailure.HostKeyChanged, "") { Offered = OfferedKey, Saved = OfferedKey with { Fingerprint = "SHA256:old" } };

        var (next, effects) = Reduce(Connected(), new ConnectionLost(problem.Message, problem));

        Assert.Equal(ConnectionStatus.Disconnected, next.Status);
        Assert.Same(problem, next.Attention);
        Assert.Equal([new StopStreamsEffect()], effects);
    }

    [Fact]
    public void A_dropped_ssh_session_reconnects_with_back_off()
    {
        var problem = new SshProblem(SshFailure.Dropped, "");

        var (next, effects) = Reduce(Connected(), new ConnectionLost(problem.Message, problem));

        Assert.Null(next.Attention);
        Assert.Contains(effects, effect => effect is ScheduleRetryEffect);
    }

    [Fact]
    public void A_list_read_that_needs_the_user_stops_too()
    {
        var problem = new SshProblem(SshFailure.SignInRefused, "") { Secret = SecretKind.Password };
        var connected = Connected();

        var (next, effects) = Reduce(connected, new LoadFailed(connected.LoadGeneration, problem.Message, EngineUnreachable: true) { Ssh = problem });

        Assert.Same(problem, next.Attention);
        Assert.DoesNotContain(effects, effect => effect is ScheduleRetryEffect);
    }
}
