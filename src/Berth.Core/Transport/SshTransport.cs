using System.Net.Sockets;
using System.Text;
using Berth.Connections;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Berth.Transport;

/// <summary>
/// Reaches an engine through an SSH host's docker CLI: each stream is one <c>docker system dial-stdio</c> on an SSH session, signed in
/// with the connection's password or key file. The session is opened on the first stream and kept until the transport is disposed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host keys.</b> A host whose key for this host and port was never accepted is only asked for its key: the key exchange runs, the key
/// is refused, and <see cref="SshFailure.HostKeyUnknown"/> carries it to the user; no user name, password or key goes to the host. A key
/// other than the accepted one is refused the same way (<see cref="SshFailure.HostKeyChanged"/>), before any sign-in.
/// </para>
/// <para>
/// <b>Secrets.</b> The password or passphrase is read from the <see cref="ISecretStore"/> when the first session signs in, only once the
/// host key is known, and kept in memory until the transport is disposed (a second session needs it, below). None missing: no
/// connection is made (<see cref="SshFailure.SecretMissing"/>). A refusal is <see cref="SshFailure.SignInRefused"/> and is never retried
/// here.
/// </para>
/// <para>
/// <b>Channels.</b> sshd allows a number of channels per session (<c>MaxSessions</c>, 10 by default), and Berth keeps several streams
/// open at once (events, logs, stats, a terminal, transfers, idle pooled connections). Up to <see cref="ChannelsPerSession"/> streams go
/// on one session; when the server refuses a channel below that, that session is taken as full at what it holds. Either way the next
/// stream goes on another session (signed in the same way, the same host key), up to <see cref="MaxSessionCount"/> sessions; a session
/// other than the first closes when its last stream does.
/// </para>
/// <para>
/// <b>Keep-alives.</b> Every session sends one every <see cref="KeepAliveInterval"/>, so a host that went away is noticed by the failing
/// send even while nothing else is sent; the session's streams then end with <see cref="SshFailure.Dropped"/>.
/// </para>
/// </remarks>
public sealed class SshTransport : IEngineTransport
{
    /// <summary>
    /// The command run for each stream: fixed text, never anything the user typed. The remote's shell runs it as the signed-in user;
    /// the docker CLI there needs to reach the engine.
    /// </summary>
    public const string DialCommand = "docker system dial-stdio";

    /// <summary>The most streams one session carries: sshd's default <c>MaxSessions</c>.</summary>
    public const int ChannelsPerSession = 10;

    /// <summary>The most SSH sessions one connection opens.</summary>
    public const int MaxSessionCount = 8;

    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly ConnectionProfile _profile;
    private readonly SshOptions _ssh;
    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Session> _sessions = [];
    private readonly int _channelsPerSession;
    private readonly SshSignInGate _signIn = new();
    private string? _password;
    private PrivateKeyFile? _key;
    private bool _disposed;

    public SshTransport(ConnectionProfile profile, ISecretStore secrets)
        : this(profile, secrets, ChannelsPerSession)
    {
    }

    /// <param name="channelsPerSession">The most streams on one session (tests lower it).</param>
    internal SshTransport(ConnectionProfile profile, ISecretStore secrets, int channelsPerSession)
    {
        if (profile.Kind != TransportKind.Ssh || profile.Ssh is null)
            throw new ArgumentException("The profile is not an SSH connection.", nameof(profile));
        _profile = profile;
        _ssh = profile.Ssh;
        _secrets = secrets;
        _channelsPerSession = channelsPerSession;
    }

    /// <summary>How many SSH sessions are open now. For tests.</summary>
    internal int SessionCount
    {
        get
        {
            lock (_sessions)
                return _sessions.Count(s => !s.Broken);
        }
    }

    public async ValueTask<Stream> OpenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (true)
            {
                var session = Usable() ?? await ConnectAsync(ct).ConfigureAwait(false);
                try
                {
                    return await Task.Run(() => session.Open(DialCommand), ct).ConfigureAwait(false);
                }
                catch (SshException ex) when (ex is not SshConnectionException && session.Count > 0)
                {
                    // The server refused one more channel: this session is full at what it holds.
                    session.Capacity = session.Count;
                }
                catch (SshException ex) when (ex is not SshConnectionException)
                {
                    throw new IOException("The SSH host refused to open a channel for the engine.", ex);
                }
                catch (ObjectDisposedException)
                {
                    // A second session closed with its last stream just as it was picked: take another.
                    session.Break();
                }
                catch (SshConnectionException ex)
                {
                    session.Break();
                    throw new SshTransportException(new SshProblem(SshFailure.Dropped, ex.Message), ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            Session[] sessions;
            lock (_sessions)
            {
                sessions = [.. _sessions];
                _sessions.Clear();
            }

            foreach (var session in sessions)
                session.Close();
            _key?.Dispose();
            _key = null;
            _password = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A session with room for one more stream, or null; a session that broke is closed and forgotten on the way.</summary>
    private Session? Usable()
    {
        Session[] broken;
        Session? usable;
        lock (_sessions)
        {
            broken = [.. _sessions.Where(s => s.Broken)];
            _sessions.RemoveAll(s => s.Broken);
            usable = _sessions.FirstOrDefault(s => s.Count < Math.Min(s.Capacity, _channelsPerSession));
        }

        foreach (var session in broken)
            session.Close();
        return usable;
    }

    /// <summary>Opens and signs in one more session; the first one checks the host key and reads the secret.</summary>
    private async Task<Session> ConnectAsync(CancellationToken ct)
    {
        _signIn.ThrowIfClosed();
        try
        {
            return await SignInAsync(ct).ConfigureAwait(false);
        }
        catch (SshTransportException ex) when (_signIn.Close(ex.Problem))
        {
            // The first refusal: no session signs in again with this secret, and the open ones end with the refusal, so the
            // event stream carries it to the session, which stops and asks.
            Session[] open;
            lock (_sessions)
                open = [.. _sessions];
            foreach (var session in open)
                session.Break(ex.Problem);
            throw;
        }
    }

    private async Task<Session> SignInAsync(CancellationToken ct)
    {
        lock (_sessions)
        {
            if (_sessions.Count >= MaxSessionCount)
                throw new IOException($"Berth already has {MaxSessionCount} SSH sessions open to this host, each full of streams.");
        }

        var saved = _ssh.SavedFor(_profile.Host, _profile.Port);
        if (saved is null)
            throw new SshTransportException(new SshProblem(SshFailure.HostKeyUnknown, "") { Offered = await ProbeHostKeyAsync(ct).ConfigureAwait(false) });

        var method = SignInMethod();
        HostKey? offered = null;
        var info = Info(method);
        PinHostKeyAlgorithms(info, saved.Type);
        var client = new SshClient(info);
        client.KeepAliveInterval = KeepAliveInterval;
        client.HostKeyReceived += (_, e) =>
        {
            offered = new HostKey(_profile.Host, _profile.Port, HostKey.TypeOf(e.HostKeyName), "SHA256:" + e.FingerPrintSHA256);
            e.CanTrust = _ssh.Check(offered) == HostKeyCheck.Known;
        };

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            if (offered is { } key && _ssh.Check(key) != HostKeyCheck.Known)
                throw new SshTransportException(new SshProblem(SshFailure.HostKeyChanged, "") { Offered = key, Saved = saved }, ex);
            throw Failure(ex);
        }

        var session = new Session(client, _sessions);
        lock (_sessions)
            _sessions.Add(session);
        return session;
    }

    /// <summary>
    /// Runs the key exchange only, to learn the host's key: the key is refused, so the connection ends before any sign-in and the
    /// user name never leaves this computer (SSH.NET sends it only in its sign-in requests).
    /// </summary>
    private async Task<HostKey> ProbeHostKeyAsync(CancellationToken ct)
    {
        HostKey? offered = null;
        using var client = new SshClient(Info(new NoneAuthenticationMethod(_ssh.User)));
        client.HostKeyReceived += (_, e) =>
        {
            offered = new HostKey(_profile.Host, _profile.Port, HostKey.TypeOf(e.HostKeyName), "SHA256:" + e.FingerPrintSHA256);
            e.CanTrust = false;
        };

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (offered is null)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw Failure(ex);
        }
        catch (Exception)
        {
            // Refused on purpose: the key is what was wanted.
        }

        return offered ?? throw new IOException("The SSH host offered no key.");
    }

    private ConnectionInfo Info(AuthenticationMethod method) =>
        new(_profile.Host, _profile.Port, _ssh.User, method)
        {
            Timeout = ConnectTimeout,
            // Berth counts the channels itself (ChannelsPerSession); SSH.NET's own count would make a stream wait instead.
            MaxSessions = 1000,
        };

    /// <summary>
    /// Asks the host only for a key of the saved type (<see cref="HostKey.AlgorithmsFor"/>). A type SSH.NET does not know leaves its
    /// list as it is: the key check still refuses any other key.
    /// </summary>
    private static void PinHostKeyAlgorithms(ConnectionInfo info, string savedType)
    {
        var wanted = HostKey.AlgorithmsFor(savedType);
        if (!wanted.Any(info.HostKeyAlgorithms.ContainsKey))
            return;
        foreach (var name in info.HostKeyAlgorithms.Keys.Where(name => !wanted.Contains(name)).ToList())
            info.HostKeyAlgorithms.Remove(name);
    }

    /// <summary>The password or the key file, read once and kept for the sessions this transport opens.</summary>
    private AuthenticationMethod SignInMethod()
    {
        if (_ssh.SignIn == SshSignIn.Password)
        {
            _password ??= _secrets.Get(_profile.Id, SecretKind.Password)
                ?? throw new SshTransportException(new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Password });
            return new PasswordAuthenticationMethod(_ssh.User, Encoding.UTF8.GetBytes(_password));
        }

        _key ??= LoadKey();
        return new PrivateKeyAuthenticationMethod(_ssh.User, _key);
    }

    private PrivateKeyFile LoadKey()
    {
        var path = _ssh.KeyPath ?? "";
        var passphrase = _secrets.Get(_profile.Id, SecretKind.Passphrase);
        try
        {
            return passphrase is null ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new SshTransportException(new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Passphrase }, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SshTransportException(new SshProblem(SshFailure.KeyFileUnreadable, ex.Message), ex);
        }
        catch (Exception ex) when (passphrase is not null)
        {
            // A wrong passphrase gives a key that does not decrypt (SSH.NET reports it in more than one way).
            throw new SshTransportException(new SshProblem(SshFailure.SignInRefused, "") { Secret = SecretKind.Passphrase }, ex);
        }
        catch (Exception ex)
        {
            throw new SshTransportException(new SshProblem(SshFailure.KeyFileUnreadable, ex.Message), ex);
        }
    }

    /// <summary>A failed connect or sign-in as the problem the user sees.</summary>
    private SshTransportException Failure(Exception ex) => ex switch
    {
        SshTransportException transport => transport,
        SshAuthenticationException => new SshTransportException(
            new SshProblem(SshFailure.SignInRefused, ex.Message) { Secret = _ssh.SignIn == SshSignIn.Password ? SecretKind.Password : null }, ex),
        SocketException or SshOperationTimeoutException or TimeoutException or OperationCanceledException =>
            new SshTransportException(new SshProblem(SshFailure.Unreachable, ex.Message), ex),
        _ => new SshTransportException(new SshProblem(SshFailure.Unreachable, ex.Message), ex),
    };

    /// <summary>One SSH session and the streams open on it.</summary>
    private sealed class Session
    {
        private readonly SshClient _client;
        private readonly List<Session> _owner;
        private readonly HashSet<SshChannelStream> _streams = [];
        private bool _broken;

        public Session(SshClient client, List<Session> owner)
        {
            _client = client;
            _owner = owner;
            client.ErrorOccurred += (_, _) => Break();
        }

        /// <summary>How many streams the server took on this session before refusing one; <see cref="int.MaxValue"/> until it does.</summary>
        public int Capacity { get; set; } = int.MaxValue;

        public int Count
        {
            get
            {
                lock (_streams)
                    return _streams.Count;
            }
        }

        public bool Broken
        {
            get
            {
                lock (_streams)
                    return _broken || !_client.IsConnected;
            }
        }

        public SshChannelStream Open(string command)
        {
            var stream = SshChannelStream.Start(_client, command, Closed);
            lock (_streams)
                _streams.Add(stream);
            return stream;
        }

        /// <summary>
        /// The session failed: every stream on it ends (with <paramref name="problem"/>, or as dropped), and it takes no more.
        /// </summary>
        public void Break(SshProblem? problem = null)
        {
            SshChannelStream[] streams;
            lock (_streams)
            {
                _broken = true;
                streams = [.. _streams];
            }

            foreach (var stream in streams)
                stream.Drop(problem);
        }

        public void Close()
        {
            Break();
            _client.Dispose();
        }

        private void Closed(SshChannelStream stream)
        {
            bool last;
            lock (_streams)
            {
                _streams.Remove(stream);
                last = _streams.Count == 0;
            }

            // A session other than the first one closes once it carries nothing.
            if (!last)
                return;
            lock (_owner)
            {
                if (_owner.IndexOf(this) <= 0)
                    return;
                _owner.Remove(this);
            }

            _client.Dispose();
        }
    }
}
