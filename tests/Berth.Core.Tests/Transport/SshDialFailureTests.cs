using Berth.Connections;
using Berth.Transport;

namespace Berth.Tests.Transport;

public sealed class SshDialFailureTests
{
    [Theory]
    [InlineData(127, "sh: docker: not found\n", SshFailure.NoDockerCli, "sh: docker: not found")]
    [InlineData(1, "bash: line 1: docker: command not found", SshFailure.NoDockerCli, "bash: line 1: docker: command not found")]
    [InlineData(1, "permission denied while trying to connect to the docker API at unix:///var/run/docker.sock", SshFailure.DockerNotPermitted,
        "permission denied while trying to connect to the docker API at unix:///var/run/docker.sock")]
    [InlineData(125, "unknown flag: --nope", SshFailure.DialFailed, "unknown flag: --nope")]
    [InlineData(1, "", SshFailure.DialFailed, "It ended with exit status 1.")]
    [InlineData(null, " ", SshFailure.DialFailed, "It ended with exit status unknown.")]
    public void A_failed_dial_is_told_apart_by_its_exit_status_and_stderr(int? exit, string stderr, SshFailure failure, string detail)
    {
        var problem = SshDialFailure.Classify(exit, stderr);

        Assert.Equal(failure, problem.Failure);
        Assert.Equal(detail, problem.Detail);
        Assert.False(problem.StopsRetry);
    }
}
