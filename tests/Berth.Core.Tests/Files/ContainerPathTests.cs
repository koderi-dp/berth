using Berth.Files;

namespace Berth.Tests.Files;

public sealed class ContainerPathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/etc", "/etc")]
    [InlineData("/etc/", "/etc")]
    [InlineData("//etc///nginx//", "/etc/nginx")]
    [InlineData("/etc/./nginx/.", "/etc/nginx")]
    [InlineData("/etc/nginx/..", "/etc")]
    [InlineData("/etc/../..", "/")]
    [InlineData("/../../x", "/x")]
    [InlineData("/a b/ c /", "/a b/ c ")]
    [InlineData("/back\\slash", "/back\\slash")]
    [InlineData("/ünï/日本", "/ünï/日本")]
    public void An_absolute_path_is_normalised_and_never_leaves_the_root(string text, string expected)
    {
        Assert.True(ContainerPath.TryParse(text, ContainerPath.Root, out var path));
        Assert.Equal(expected, path.Value);
    }

    [Theory]
    [InlineData("/etc", "nginx", "/etc/nginx")]
    [InlineData("/etc", "../usr", "/usr")]
    [InlineData("/etc", "..", "/")]
    [InlineData("/", "..", "/")]
    [InlineData("/etc", ".", "/etc")]
    [InlineData("/etc", "", "/etc")]
    [InlineData("/etc", "/var/log", "/var/log")]
    public void A_relative_path_is_read_from_the_current_folder(string from, string text, string expected)
    {
        Assert.True(ContainerPath.TryParse(from, ContainerPath.Root, out var current));
        Assert.True(ContainerPath.TryParse(text, current, out var path));
        Assert.Equal(expected, path.Value);
    }

    [Fact]
    public void A_path_with_a_nul_is_refused()
    {
        Assert.False(ContainerPath.TryParse("/a\0b", ContainerPath.Root, out _));
    }

    [Fact]
    public void A_line_break_at_the_end_of_a_pasted_path_is_dropped_but_one_inside_a_name_is_kept()
    {
        Assert.True(ContainerPath.TryParse("/etc\r\n", ContainerPath.Root, out var pasted));
        Assert.Equal("/etc", pasted.Value);
        Assert.True(ContainerPath.TryParse("/new\nline/x", ContainerPath.Root, out var inside));
        Assert.Equal("/new\nline/x", inside.Value);
    }

    [Fact]
    public void The_parent_of_the_root_is_the_root()
    {
        Assert.Equal(ContainerPath.Root, ContainerPath.Root.Parent);
        Assert.True(ContainerPath.Root.IsRoot);
        Assert.Equal("/", ContainerPath.Root.Name);
    }

    [Fact]
    public void Parent_and_name_split_the_last_segment()
    {
        var path = ContainerPath.Root.Join("etc").Join("nginx");
        Assert.Equal("/etc/nginx", path.Value);
        Assert.Equal("nginx", path.Name);
        Assert.Equal("/etc", path.Parent.Value);
        Assert.Equal("/", path.Parent.Parent.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\0")]
    public void Join_takes_one_plain_name(string name)
    {
        Assert.Throws<ArgumentException>(() => ContainerPath.Root.Join(name));
    }

    [Fact]
    public void Join_keeps_awkward_names_whole()
    {
        Assert.Equal("/-dash", ContainerPath.Root.Join("-dash").Value);
        Assert.Equal("/a b", ContainerPath.Root.Join("a b").Value);
        Assert.Equal("/new\nline", ContainerPath.Root.Join("new\nline").Value);
        Assert.Equal("/...", ContainerPath.Root.Join("...").Value);
    }

    [Fact]
    public void Segments_are_the_breadcrumb_from_the_root()
    {
        Assert.True(ContainerPath.TryParse("/etc/nginx/conf.d", ContainerPath.Root, out var path));
        var segments = path.Segments;
        Assert.Equal(["/", "etc", "nginx", "conf.d"], segments.Select(s => s.Name));
        Assert.Equal(["/", "/etc", "/etc/nginx", "/etc/nginx/conf.d"], segments.Select(s => s.Path.Value));
        Assert.Equal(["/"], ContainerPath.Root.Segments.Select(s => s.Name));
    }

    [Fact]
    public void Paths_compare_by_value_and_default_is_the_root()
    {
        Assert.Equal(ContainerPath.Root.Join("a"), ContainerPath.Root.Join("a"));
        Assert.NotEqual(ContainerPath.Root.Join("a"), ContainerPath.Root.Join("A"));
        Assert.Equal("/", default(ContainerPath).Value);
        Assert.Equal(ContainerPath.Root, default);
    }
}
