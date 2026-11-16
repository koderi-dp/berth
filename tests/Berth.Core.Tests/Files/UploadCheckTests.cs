using Berth.Files;

namespace Berth.Tests.Files;

/// <summary>Which of an upload's names would replace an entry the user has not said yes to, as the container is just before sending.</summary>
public sealed class UploadCheckTests
{
    [Fact]
    public void Nothing_there_asks_nothing()
    {
        Assert.Empty(UploadCheck.Unconfirmed(["a.txt", "site"], new HashSet<string>(), []));
    }

    [Fact]
    public void A_name_that_is_there_and_not_confirmed_is_asked_about()
    {
        Assert.Equal(["a.txt"], UploadCheck.Unconfirmed(["a.txt", "b.txt"], new HashSet<string> { "a.txt" }, []));
    }

    [Fact]
    public void A_confirmed_name_is_not_asked_again_but_one_that_appeared_since_is()
    {
        Assert.Equal(["b.txt"], UploadCheck.Unconfirmed(["a.txt", "b.txt"], new HashSet<string> { "a.txt", "b.txt" }, ["a.txt"]));
    }

    [Fact]
    public void Names_compare_as_linux_does_case_and_all()
    {
        Assert.Empty(UploadCheck.Unconfirmed(["Site"], new HashSet<string>(StringComparer.Ordinal) { "site" }, []));
        Assert.Equal(["site"], UploadCheck.Unconfirmed(["site"], new HashSet<string>(StringComparer.Ordinal) { "site" }, ["Site"]));
    }

    [Fact]
    public void The_names_sent_are_the_sources_last_segments()
    {
        Assert.Equal(["a.txt", "site"], UploadCheck.NamesOf([@"C:\x\a.txt", @"C:\x\site\"]));
    }
}
