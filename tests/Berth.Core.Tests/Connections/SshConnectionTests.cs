using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class SshConnectionTests
{
    private static readonly HostKey Accepted = new("host", 2222, "ssh-ed25519", "SHA256:Q90nzWlu3QhV+ZrycI9H0M/QxcWAxT4EJxSwFm9nlxc");
    private static readonly HostKey Other = new("host", 2222, "ssh-ed25519", "SHA256:dZjvWQSBkDdehxXuu7ud9UGQypFvs3auCP1i7Mn4D5Y");

    [Fact]
    public void Ssh_options_compare_by_their_saved_keys_not_by_the_list_instance()
    {
        var one = new SshOptions("berth", SshSignIn.Password, null, [Accepted]);
        var two = new SshOptions("berth", SshSignIn.Password, null, [Accepted with { }]);

        Assert.Equal(one, two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
        Assert.NotEqual(one, two with { KnownHosts = [Other] });
    }

    [Fact]
    public void A_key_offered_by_a_host_is_known_unknown_or_changed()
    {
        var options = new SshOptions("berth", SshSignIn.Password, null, [Accepted]);

        Assert.Equal(HostKeyCheck.Known, options.Check(Accepted with { Host = "HOST" }));
        Assert.Equal(HostKeyCheck.Unknown, options.Check(Accepted with { Port = 22 }));
        Assert.Equal(HostKeyCheck.Changed, options.Check(Other));
        Assert.Equal(HostKeyCheck.Changed, options.Check(Accepted with { Type = "ecdsa-sha2-nistp256" }));
        Assert.Equal(Accepted, options.SavedFor("host", 2222));
    }

    [Fact]
    public void Accepting_a_key_saves_it_once_for_its_host_and_port()
    {
        var options = new SshOptions("berth", SshSignIn.Password, null, []);

        var accepted = options.Accept(Accepted);

        Assert.Equal<HostKey>([Accepted], accepted.KnownHosts);
        Assert.Same(accepted, accepted.Accept(Accepted));
    }

    [Fact]
    public void A_changed_key_is_never_accepted_in_place_of_the_saved_one()
    {
        var options = new SshOptions("berth", SshSignIn.Password, null, [Accepted]);

        var after = options.Accept(Other);

        Assert.Same(options, after);
        Assert.Equal<HostKey>([Accepted], after.KnownHosts);
    }

    [Fact]
    public void Removing_a_saved_key_lets_the_new_one_be_accepted()
    {
        var options = new SshOptions("berth", SshSignIn.Password, null, [Accepted]);

        var after = options.Remove(Accepted).Accept(Other);

        Assert.Equal<HostKey>([Other], after.KnownHosts);
    }

    [Theory]
    [InlineData("ssh-rsa", "ssh-rsa")]
    [InlineData("rsa-sha2-512", "ssh-rsa")]
    [InlineData("rsa-sha2-256", "ssh-rsa")]
    [InlineData("ssh-ed25519", "ssh-ed25519")]
    [InlineData("ecdsa-sha2-nistp256", "ecdsa-sha2-nistp256")]
    public void The_key_type_is_the_key_s_not_the_signature_algorithm(string algorithm, string type) =>
        Assert.Equal(type, HostKey.TypeOf(algorithm));

    [Fact]
    public void A_problem_that_needs_the_user_stops_the_retries()
    {
        Assert.True(new SshProblem(SshFailure.HostKeyUnknown, "") { Offered = Accepted }.StopsRetry);
        Assert.True(new SshProblem(SshFailure.HostKeyChanged, "") { Offered = Other, Saved = Accepted }.StopsRetry);
        Assert.True(new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Password }.StopsRetry);
        Assert.True(new SshProblem(SshFailure.SignInRefused, "") { Secret = SecretKind.Password }.StopsRetry);
        Assert.False(new SshProblem(SshFailure.Unreachable, "refused").StopsRetry);
        Assert.False(new SshProblem(SshFailure.NoDockerCli, "sh: docker: not found").StopsRetry);
        Assert.False(new SshProblem(SshFailure.Dropped, "").StopsRetry);
    }

    [Fact]
    public void Each_problem_has_its_own_message_and_the_remote_s_words_as_they_were()
    {
        var messages = new[]
        {
            new SshProblem(SshFailure.Unreachable, "No connection could be made."),
            new SshProblem(SshFailure.HostKeyUnknown, "") { Offered = Accepted },
            new SshProblem(SshFailure.HostKeyChanged, "") { Offered = Other, Saved = Accepted },
            new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Password },
            new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Passphrase },
            new SshProblem(SshFailure.SignInRefused, "") { Secret = SecretKind.Password },
            new SshProblem(SshFailure.SignInRefused, "") { Secret = SecretKind.Passphrase },
            new SshProblem(SshFailure.SignInRefused, ""),
            new SshProblem(SshFailure.NoDockerCli, "sh: docker: not found"),
            new SshProblem(SshFailure.DialFailed, "unknown flag"),
            new SshProblem(SshFailure.DockerNotPermitted, "permission denied while trying to connect"),
            new SshProblem(SshFailure.Dropped, ""),
            new SshProblem(SshFailure.KeyFileUnreadable, ""),
        }.Select(p => p.Message).ToList();

        Assert.Equal(messages.Count, messages.Distinct().Count());
        Assert.Contains("sh: docker: not found", messages[8], StringComparison.Ordinal);
        Assert.Contains("permission denied while trying to connect", messages[10], StringComparison.Ordinal);
        Assert.Contains(Accepted.Fingerprint, messages[2], StringComparison.Ordinal);
        Assert.Contains(Other.Fingerprint, messages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_remote_s_words_are_bounded()
    {
        var problem = new SshProblem(SshFailure.DialFailed, new string('x', 10_000));

        Assert.True(problem.Detail.Length <= SshProblem.DetailLimit + 1);
        Assert.EndsWith("…", problem.Detail, StringComparison.Ordinal);
    }
}
