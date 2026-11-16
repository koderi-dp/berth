using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Berth.Text;

namespace Berth.Tests.Text;

public sealed partial class ExternalTerminalTests
{
    private const string Id = "4f1c2a9b7d3e";

    private const string CannotPass = "This command cannot be passed to an external terminal unchanged. Copy it instead.";

    private static ImmutableArray<string> Docker(params string[] command) => ["-H", "tcp://127.0.0.1:2375", "exec", "-it", Id, .. command];

    [Fact]
    public void Windows_terminal_runs_docker_in_a_titled_new_tab()
    {
        var launch = ExternalTerminal.WindowsTerminal("web-1", Docker("sh"));

        Assert.Equal("wt.exe", launch.FileName);
        Assert.False(launch.UseShellExecute);
        Assert.Equal<string>(
            ["new-tab", "--title", "web-1", "--suppressApplicationTitle", "docker", "-H", "tcp://127.0.0.1:2375", "exec", "-it", Id, "sh"],
            launch.Arguments);
    }

    [Fact]
    public void A_semicolon_in_an_argument_is_escaped()
    {
        var launch = ExternalTerminal.WindowsTerminal("web;1", Docker("sh", "-c", "echo a b; sleep 5", @"a\;b", ";", "x;"));

        Assert.Equal(@"web\;1", launch.Arguments[2]);
        Assert.Equal<string>(["sh", "-c", @"echo a b\; sleep 5", @"a\\;b", @"\;", @"x\;"], launch.Arguments.TakeLast(6));
    }

    [Fact]
    public void The_own_console_runs_docker_with_shell_execute()
    {
        var launch = ExternalTerminal.OwnConsole(Docker("sh", "-c", "echo a; b"));

        Assert.Equal("docker.exe", launch.FileName);
        Assert.True(launch.UseShellExecute);
        Assert.Equal<string>(Docker("sh", "-c", "echo a; b"), launch.Arguments);
    }

    [Fact]
    public void A_title_is_required()
    {
        Assert.Throws<ArgumentException>(() => ExternalTerminal.WindowsTerminal("", Docker("sh")));
    }

    // Two parsers stand between Berth and docker in Windows Terminal: wt splits its own command line, unescapes "\;", joins the
    // command's words again (quoting only a word with a space, escaping nothing), expands %VARIABLES% and starts it; then docker
    // splits that line by the C runtime's rules. WindowsTerminalPath models that path (from Windows Terminal's source) and checks
    // that what docker gets is what Berth meant.

    [Theory]
    [InlineData("sh")]
    [InlineData("echo a b; sleep 5")]
    [InlineData(@"C:\Users\a b\ca.pem")]
    [InlineData(@"C:\Users\ab\")]
    [InlineData(@"a\\b")]
    [InlineData(@"a\;b")]
    [InlineData(@"a\\;b")]
    [InlineData(";")]
    [InlineData(";;")]
    [InlineData(" ; ")]
    [InlineData("it's 'quoted'")]
    [InlineData("$HOME `id` $(id) & | < > ^ !")]
    [InlineData("-c")]
    [InlineData("--title")]
    [InlineData("naïve – ünïcode 日本")]
    public void An_argument_that_is_passed_reaches_docker_unchanged(string word)
    {
        var docker = Docker("sh", "-c", word);
        Assert.Null(ExternalTerminal.Refusal(docker));

        var launch = ExternalTerminal.WindowsTerminal("web", docker);

        Assert.Equal<string>(docker, WindowsTerminalPath.DockerArguments(launch.Arguments));
    }

    [Fact]
    public void A_tls_connection_with_certificate_paths_that_hold_spaces_reaches_docker_unchanged()
    {
        ImmutableArray<string> docker =
        [
            "--tlsverify", "--tlscacert", @"C:\Users\a b\certs\ca.pem", "--tlscert", @"C:\Users\a b\certs\cert.pem",
            "--tlskey", @"C:\Users\a b\certs\key.pem", "-H", "tcp://[fd00::1]:2376", "exec", "-it", Id, "sh",
        ];

        var launch = ExternalTerminal.WindowsTerminal("web", docker);

        Assert.Equal<string>(docker, WindowsTerminalPath.DockerArguments(launch.Arguments));
    }

    [Theory]
    [InlineData("say \"hi\"")]
    [InlineData("\"")]
    [InlineData("%PATH%")]
    [InlineData("100%")]
    [InlineData(@"C:\a b\")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("")]
    [InlineData("--")]
    public void An_argument_one_of_the_parsers_would_change_is_refused(string word)
    {
        var docker = Docker("sh", "-c", word);

        Assert.Equal(CannotPass, ExternalTerminal.Refusal(docker));
        Assert.Throws<ArgumentException>(() => ExternalTerminal.WindowsTerminal("web", docker));
        Assert.Throws<ArgumentException>(() => ExternalTerminal.OwnConsole(docker));
    }

    [Fact]
    public void A_lone_surrogate_is_refused_and_a_pair_is_not()
    {
        Assert.Equal(CannotPass, ExternalTerminal.Refusal(Docker("sh", "-c", "a" + (char)0xD800 + "b")));
        Assert.Equal(CannotPass, ExternalTerminal.Refusal(Docker("sh", "-c", "a" + (char)0xDC00)));
        Assert.Null(ExternalTerminal.Refusal(Docker("sh", "-c", char.ConvertFromUtf32(0x1F600))));
    }

    [Theory]
    [InlineData("say \"hi\"")]
    [InlineData("%PATH%")]
    [InlineData(@"C:\a b\")]
    public void What_is_refused_would_indeed_reach_docker_changed(string word)
    {
        // Built by hand, past the refusal: shows the refusal guards a real change, not a guess.
        ImmutableArray<string> wt = ["new-tab", "--title", "web", "--suppressApplicationTitle", "docker", "sh", "-c", word];

        Assert.NotEqual<string>(["sh", "-c", word], WindowsTerminalPath.DockerArguments(wt));
    }

    /// <summary>The way from wt.exe's argument list to the arguments docker.exe receives, as Windows Terminal's source does it.</summary>
    private static partial class WindowsTerminalPath
    {
        public static string[] DockerArguments(ImmutableArray<string> wtArguments)
        {
            // .NET joins the list so that CommandLineToArgvW gives it back unchanged; wt splits its line with CommandLineToArgvW.
            var argv = CommandLineToArgv(Join(wtArguments))[1..];
            Assert.Equal<string>(wtArguments, argv);

            // AppCommandlineArgs::_addCommandsForArg: an unescaped ';' starts the next wt command. Commandline::AddArg: "\;" -> ";".
            Assert.DoesNotContain(argv, a => Delimiter().IsMatch(a));
            var words = argv.Select(a => a.Replace(@"\;", ";", StringComparison.Ordinal)).ToArray();

            // _getNewTerminalArgs: the command's words joined by spaces, a word with a space in double quotes, nothing escaped.
            var command = words.SkipWhile(w => w != "docker").ToArray();
            var line = string.Join(' ', command.Select(w => w.Contains(' ', StringComparison.Ordinal) ? $"\"{w}\"" : w));

            // ConptyConnection: ExpandEnvironmentStringsW on the line, then CreateProcessW; docker splits it as the C runtime does.
            var docker = CommandLineToArgv(Environment.ExpandEnvironmentVariables(line));
            Assert.Equal("docker", docker[0]);
            return docker[1..];
        }

        [GeneratedRegex(@"^;|[^\\];")]
        private static partial Regex Delimiter();

        /// <summary>Joined as .NET's ProcessStartInfo.ArgumentList is (PasteArguments).</summary>
        private static string Join(IEnumerable<string> arguments)
        {
            var text = new StringBuilder("wt.exe");
            foreach (var argument in arguments)
            {
                text.Append(' ');
                if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
                {
                    text.Append(argument);
                    continue;
                }

                text.Append('"');
                var slashes = 0;
                foreach (var c in argument)
                {
                    if (c == '\\')
                    {
                        slashes++;
                        continue;
                    }

                    text.Append('\\', c == '"' ? (slashes * 2) + 1 : slashes).Append(c);
                    slashes = 0;
                }

                text.Append('\\', slashes * 2).Append('"');
            }

            return text.ToString();
        }

        private static string[] CommandLineToArgv(string line)
        {
            var list = NativeCommandLineToArgv(line, out var count);
            Assert.NotEqual(IntPtr.Zero, list);
            try
            {
                return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size))!)];
            }
            finally
            {
                LocalFree(list);
            }
        }

        [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr NativeCommandLineToArgv(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
