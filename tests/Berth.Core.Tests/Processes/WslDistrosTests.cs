using System.Text;
using Berth.Processes;

namespace Berth.Tests.Processes;

public sealed class WslDistrosTests
{
    [Fact]
    public void Reads_the_names_wsl_lists_in_utf16_in_order()
    {
        // As `wsl.exe -l -q` writes them: UTF-16 little-endian, no byte order mark, CRLF after each name.
        var output = Encoding.Unicode.GetBytes("Ubuntu-24.04\r\nDebian\r\n");

        Assert.Equal(["Ubuntu-24.04", "Debian"], WslDistros.Parse(output));
    }

    [Fact]
    public void Reads_utf8_when_wsl_is_told_to_write_it()
    {
        Assert.Equal(["Ubuntu-24.04"], WslDistros.Parse(Encoding.UTF8.GetBytes("Ubuntu-24.04\n")));
    }

    [Fact]
    public void Skips_blank_lines_stray_nulls_and_the_docker_desktop_distros()
    {
        var output = Encoding.Unicode.GetBytes("\r\nUbuntu-24.04 \r\ndocker-desktop\r\ndocker-desktop-data\r\n\0\r\n");

        Assert.Equal(["Ubuntu-24.04"], WslDistros.Parse(output));
    }

    [Fact]
    public void No_output_is_no_distro()
    {
        Assert.Empty(WslDistros.Parse([]));
    }
}
