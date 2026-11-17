using Berth.Connections;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class SshPromptsTests
{
    private static readonly HostKey Saved = new("build-host", 22, "ssh-ed25519", "SHA256:Q90nzWlu3QhV+ZrycI9H0M/QxcWAxT4EJxSwFm9nlxc");
    private static readonly HostKey Offered = new("build-host", 22, "ecdsa-sha2-nistp256", "SHA256:dZjvWQSBkDdehxXuu7ud9UGQypFvs3auCP1i7Mn4D5Y");

    private static EngineState Asking(SshProblem problem) =>
        EngineState.Initial with { ConnectionError = problem.Message, Attention = problem };

    [Theory]
    [InlineData("ssh-ed25519", "ED25519")]
    [InlineData("ssh-rsa", "RSA")]
    [InlineData("ecdsa-sha2-nistp384", "ECDSA")]
    [InlineData("sk-ssh-ed25519@openssh.com", "sk-ssh-ed25519@openssh.com")]
    public void Key_types_are_named_as_ssh_keygen_names_them(string type, string name) => Assert.Equal(name, SshPrompts.KeyType(type));

    [Fact]
    public void The_question_shows_the_type_and_the_fingerprint_as_ssh_keygen_prints_it()
    {
        var question = SshPrompts.Question(Offered with { Port = 2222 });

        Assert.Equal("ECDSA", question.KeyType);
        Assert.Equal("SHA256:dZjvWQSBkDdehxXuu7ud9UGQypFvs3auCP1i7Mn4D5Y", question.Fingerprint);
        Assert.Contains("build-host port 2222", question.Intro, StringComparison.Ordinal);
        Assert.Contains("ssh-keygen -lf", question.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_changed_key_warning_names_both_keys_and_the_way_on_through_settings()
    {
        var warning = SshPrompts.Changed(Saved, Offered);

        Assert.Equal("Accepted before: ED25519 key SHA256:Q90nzWlu3QhV+ZrycI9H0M/QxcWAxT4EJxSwFm9nlxc", warning.SavedLine);
        Assert.Equal("Offered now: ECDSA key SHA256:dZjvWQSBkDdehxXuu7ud9UGQypFvs3auCP1i7Mn4D5Y", warning.OfferedLine);
        Assert.Contains("Settings", warning.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_needs_attention_with_the_question_and_no_retry()
    {
        var view = EngineStatus.Disconnected(Asking(new SshProblem(SshFailure.HostKeyUnknown, "") { Offered = Offered }));

        Assert.Equal(EnginePhase.NeedsAttention, EngineStatus.Phase(Asking(new SshProblem(SshFailure.HostKeyUnknown, "") { Offered = Offered })));
        Assert.Equal("Needs attention", EngineStatus.Text(EnginePhase.NeedsAttention));
        Assert.Equal(new SshPrompt(SshPromptKind.AcceptHostKey, "Check the host's key", "Check the key…"), view.Prompt);
        Assert.False(view.CanRetry);
        Assert.False(view.Busy);
    }

    [Fact]
    public void A_changed_key_shows_the_warning_and_retry_only_checks_again()
    {
        var view = EngineStatus.Disconnected(Asking(new SshProblem(SshFailure.HostKeyChanged, "") { Offered = Offered, Saved = Saved }));

        Assert.Equal(SshPromptKind.ChangedHostKey, view.Prompt?.Kind);
        Assert.True(view.CanRetry);
    }

    [Fact]
    public void A_refused_password_asks_again_and_offers_no_retry_of_the_kept_one()
    {
        var problem = new SshProblem(SshFailure.SignInRefused, "Permission denied (password).") { Secret = SecretKind.Password };
        var view = EngineStatus.Disconnected(Asking(problem));
        var question = SshPrompts.Secret(problem, "berth", "build-host", 22);

        Assert.Equal(new SshPrompt(SshPromptKind.EnterSecret, "Sign-in needed", "Enter password…"), view.Prompt);
        Assert.False(view.CanRetry);
        Assert.Equal("Enter the password", question.Title);
        Assert.Equal("The host refused the password for berth@build-host. Berth did not try it again.", question.Note);
    }

    [Fact]
    public void A_missing_passphrase_asks_for_the_passphrase()
    {
        var problem = new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Passphrase };

        Assert.Equal("Enter passphrase…", SshPrompts.For(problem)?.Action);
        Assert.Equal("Passphrase of the key file", SshPrompts.Secret(problem, "berth", "h", 22).Label);
    }

    [Fact]
    public void A_refused_key_file_has_no_dialog_and_retry_tries_it_again()
    {
        var view = EngineStatus.Disconnected(Asking(new SshProblem(SshFailure.SignInRefused, "Permission denied (publickey).")));

        Assert.Null(view.Prompt);
        Assert.True(view.CanRetry);
        Assert.Equal("Sign-in refused", view.Title);
    }
}
