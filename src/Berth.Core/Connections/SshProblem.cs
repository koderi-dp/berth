namespace Berth.Connections;

/// <summary>Why an SSH connection failed, as far as the session and the user need to tell the cases apart.</summary>
public enum SshFailure
{
    /// <summary>The host was not reached (no answer, refused, name not found, timed out).</summary>
    Unreachable,

    /// <summary>The host offered a key this connection has not accepted: the user is asked (<see cref="SshProblem.Offered"/>).</summary>
    HostKeyUnknown,

    /// <summary>The host offered another key than the one accepted: blocked until the saved key is removed.</summary>
    HostKeyChanged,

    /// <summary>No password, or no passphrase for a key file that needs one, is kept: the user is asked (<see cref="SshProblem.Secret"/>).</summary>
    SecretMissing,

    /// <summary>
    /// The host refused the user name and password or the key, or the passphrase does not open the key file. Not tried again until the
    /// user acts: three refusals can lock an account.
    /// </summary>
    SignInRefused,

    /// <summary>The remote has no <c>docker</c> command.</summary>
    NoDockerCli,

    /// <summary><c>docker system dial-stdio</c> failed on the remote for another reason.</summary>
    DialFailed,

    /// <summary>The remote user may not use the engine (its socket refused them).</summary>
    DockerNotPermitted,

    /// <summary>The SSH session ended under a stream.</summary>
    Dropped,
}

/// <summary>
/// An SSH connection's failure, with what the user needs to act on it. <see cref="Detail"/> is what the host, the remote command or the
/// network said, as-is and bounded; it never holds a secret (Berth never puts one in a message, and the remote never sees one in its
/// command line).
/// </summary>
public sealed record SshProblem
{
    /// <summary>The most of <see cref="Detail"/> that is kept: the remote's stderr can be long.</summary>
    public const int DetailLimit = 2000;

    public SshProblem(SshFailure failure, string detail)
    {
        Failure = failure;
        Detail = detail.Length > DetailLimit ? detail[..DetailLimit] + "…" : detail;
    }

    public SshFailure Failure { get; }

    public string Detail { get; }

    /// <summary>The key the host offered, for <see cref="SshFailure.HostKeyUnknown"/> and <see cref="SshFailure.HostKeyChanged"/>.</summary>
    public HostKey? Offered { get; init; }

    /// <summary>The key accepted before, for <see cref="SshFailure.HostKeyChanged"/>.</summary>
    public HostKey? Saved { get; init; }

    /// <summary>
    /// The secret to ask for, for <see cref="SshFailure.SecretMissing"/>, and for <see cref="SshFailure.SignInRefused"/> when a new one
    /// can help (a refused password, a wrong passphrase); null when the host refused the key itself.
    /// </summary>
    public SecretKind? Secret { get; init; }

    /// <summary>The session must not connect again on its own: the user decides (a key to accept or a secret to enter) first.</summary>
    public bool StopsRetry => Failure is SshFailure.HostKeyUnknown or SshFailure.HostKeyChanged or SshFailure.SecretMissing or SshFailure.SignInRefused;

    /// <summary>The failure in a sentence or two for the user, ending with what the remote said where it said something.</summary>
    public string Message => Failure switch
    {
        SshFailure.Unreachable => With("The SSH host could not be reached.", Detail),
        SshFailure.HostKeyUnknown => $"Berth has not seen this host's key before ({Offered?.Fingerprint}). Check it and accept it to connect.",
        SshFailure.HostKeyChanged =>
            $"The host's key has changed: it offered {Offered?.Fingerprint}, but the key accepted before is {Saved?.Fingerprint}. " +
            "Berth will not connect. If the change is expected, remove the saved key in Settings and connect again.",
        SshFailure.SecretMissing when Secret == SecretKind.Passphrase => "Enter the passphrase of the key file to connect.",
        SshFailure.SecretMissing => "Enter the password to connect.",
        SshFailure.SignInRefused when Secret == SecretKind.Passphrase =>
            "The passphrase does not open the key file. Berth will not try it again: enter the passphrase.",
        SshFailure.SignInRefused when Secret == SecretKind.Password =>
            "The host refused the user name or password. Berth will not try it again: enter the password.",
        SshFailure.SignInRefused => With("The host refused the key file. Berth will not try it again until you retry.", Detail),
        SshFailure.NoDockerCli => With("The remote host has no docker command. Install the docker CLI there.", Detail),
        SshFailure.DialFailed => With("docker system dial-stdio failed on the remote host.", Detail),
        SshFailure.DockerNotPermitted => With("The remote user may not use docker.", Detail),
        _ => With("The SSH connection was lost.", Detail),
    };

    private static string With(string sentence, string detail) => detail.Length == 0 ? sentence : sentence + "\n" + detail;
}
