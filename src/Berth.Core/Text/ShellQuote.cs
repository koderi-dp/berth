using System.Text;

namespace Berth.Text;

/// <summary>How a value is written in a command for the user to paste, so the shell passes it through unchanged.</summary>
public static class ShellQuote
{
    /// <summary>
    /// A value (every argument but docker's own option names) bare when it is made only of letters, digits and
    /// <c>_ - . / : = @ % +</c> and, for PowerShell, does not start with "@", which would splat, or "-", which PowerShell can take for
    /// a parameter ("--%" stops its parsing, "-a.b" is split in two); otherwise in single quotes, inside which neither shell acts on
    /// anything but the quote itself.
    /// </summary>
    public static string Quote(string value, ShellKind shell)
    {
        if (value.Length > 0 && value.All(IsSafe) && !(shell == ShellKind.PowerShell && value[0] is '@' or '-'))
        {
            return value;
        }

        var text = new StringBuilder("'");
        foreach (var c in value)
        {
            if (shell == ShellKind.Posix && c == '\'')
            {
                text.Append(@"'\''");
            }
            else if (shell == ShellKind.PowerShell && IsPowerShellSingleQuote(c))
            {
                // PowerShell ends a single-quoted string at any of its single-quote characters; doubling one keeps it.
                text.Append(c).Append(c);
            }
            else
            {
                text.Append(c);
            }
        }

        return text.Append('\'').ToString();
    }

    private static bool IsSafe(char c) => char.IsAsciiLetterOrDigit(c) || "_-./:=@%+".Contains(c, StringComparison.Ordinal);

    private static bool IsPowerShellSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';
}

/// <summary>The shell a copied command is written for.</summary>
public enum ShellKind
{
    /// <summary>PowerShell: a single quote inside single quotes is doubled.</summary>
    PowerShell,

    /// <summary>A POSIX shell such as bash or zsh: a single quote inside single quotes is written <c>'\''</c>.</summary>
    Posix,
}
