using Berth.Session;

namespace Berth.Tests.Session;

public sealed class ExecCleanUpTests
{
    [Fact]
    public void A_marker_is_sixteen_lowercase_hex_characters_and_new_each_time()
    {
        var markers = Enumerable.Range(0, 50).Select(_ => ExecCleanUp.NewMarker()).ToArray();

        Assert.All(markers, m => Assert.Matches("^[0-9a-f]{16}$", m));
        Assert.Equal(markers.Length, markers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_variable_names_the_marker()
    {
        Assert.Equal("BERTH_EXEC=0123456789abcdef", ExecCleanUp.Variable("0123456789abcdef"));
    }

    [Fact]
    public void The_command_passes_the_marker_as_an_argument_not_in_the_script()
    {
        const string Marker = "0123456789abcdef";

        var command = ExecCleanUp.Command(Marker);

        Assert.Equal(["sh", "-c", ExecCleanUp.Script, "berth-clean-up", Marker], command.ToArray());
        Assert.DoesNotContain(Marker, ExecCleanUp.Script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcde")]
    [InlineData("0123456789abcdef0")]
    [InlineData("0123456789abcde;")]
    public void Anything_but_a_marker_is_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => ExecCleanUp.Command(value));
        Assert.Throws<ArgumentException>(() => ExecCleanUp.Variable(value));
    }

    [Fact]
    public void The_script_hangs_up_only_exact_matches_and_spares_itself_and_pid_one()
    {
        var script = ExecCleanUp.Script;

        Assert.DoesNotContain((char)13, script);
        Assert.Contains("m=\"BERTH_EXEC=$1\"", script, StringComparison.Ordinal);
        Assert.Contains("grep -qxF -- \"$m\"", script, StringComparison.Ordinal);
        Assert.Contains("case \"$p\" in 1|\"$$\") continue;; esac", script, StringComparison.Ordinal);
        Assert.Contains("kill -HUP", script, StringComparison.Ordinal);
        // The id is checked again in the container: anything but lowercase hex ends the script before /proc is read.
        Assert.StartsWith("case \"$1\" in \"\"|*[!0-9a-f]*) exit 2;; esac", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_never_kills_what_ignores_the_hang_up()
    {
        // Closing a terminal hangs up; what the user protected with nohup (or that handles HUP itself) stays, as with docker exec.
        Assert.DoesNotContain("KILL", ExecCleanUp.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("sleep", ExecCleanUp.Script, StringComparison.Ordinal);
    }
}
