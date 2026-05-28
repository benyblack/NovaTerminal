using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class RemotePathAutocompleteQueryTests
{
    [Fact]
    public void Parse_WhenGivenPartialAbsolutePath_SplitsParentAndPrefix()
    {
        RemotePathAutocompleteQuery query = RemotePathAutocompleteQuery.Parse("/mnt/media/mov");

        Assert.Equal("/mnt/media", query.ParentPath);
        Assert.Equal("mov", query.Prefix);
    }

    [Fact]
    public void Parse_WhenGivenTildePathWithoutSeparator_UsesTildeAsParent()
    {
        RemotePathAutocompleteQuery query = RemotePathAutocompleteQuery.Parse("~/Do");

        Assert.Equal("~", query.ParentPath);
        Assert.Equal("Do", query.Prefix);
    }

    [Fact]
    public void Parse_WhenPathEndsWithSeparator_UsesDirectoryAsParentAndEmptyPrefix()
    {
        RemotePathAutocompleteQuery query = RemotePathAutocompleteQuery.Parse("~/code/");

        Assert.Equal("~/code", query.ParentPath);
        Assert.Equal(string.Empty, query.Prefix);
    }

    [Fact]
    public void Parse_WhenRootPathEndsWithSeparator_UsesRootAsParentAndEmptyPrefix()
    {
        RemotePathAutocompleteQuery query = RemotePathAutocompleteQuery.Parse("/");

        Assert.Equal("/", query.ParentPath);
        Assert.Equal(string.Empty, query.Prefix);
    }

    [Fact]
    public void Rank_PrefersDirectoryPrefixMatchesBeforeFiles()
    {
        IReadOnlyList<RemotePathSuggestion> suggestions = RemotePathAutocompleteQuery.Rank(
            new[]
            {
                new RemotePathSuggestion("movie.mkv", "/mnt/media/movie.mkv", isDirectory: false),
                new RemotePathSuggestion("movies", "/mnt/media/movies", isDirectory: true)
            },
            prefix: "mov");

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("movies", suggestions[0].DisplayName);
        Assert.True(suggestions[0].IsDirectory);
    }
}
