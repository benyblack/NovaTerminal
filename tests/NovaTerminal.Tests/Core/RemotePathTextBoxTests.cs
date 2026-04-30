using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Core;

public sealed class RemotePathTextBoxTests
{
    [AvaloniaFact]
    public async Task RemotePathTextBox_LoadsSuggestions_WhenProfileAndSessionArePresent()
    {
        var control = new RemotePathTextBox
        {
            ProfileId = Guid.Parse("04b9578d-ecb6-4e25-8ef4-a8212c57de7d"),
            SessionId = Guid.Parse("cdc282f9-af28-4ad9-b3c2-9164bf9251d6"),
            AutocompleteService = new FakeRemotePathAutocompleteService(
                new RemotePathSuggestion("Downloads", "~/Downloads", isDirectory: true))
        };

        control.SetTextForTest("~/Do");

        await control.RefreshSuggestionsForTestAsync();

        Assert.True(control.AreSuggestionsOpenForTest);
        Assert.Collection(
            control.GetSuggestionsForTest(),
            suggestion => Assert.Equal("Downloads", suggestion.DisplayName));
    }

    [AvaloniaFact]
    public async Task RemotePathTextBox_AcceptsDirectorySuggestion_WithTrailingSlash()
    {
        var control = new RemotePathTextBox
        {
            ProfileId = Guid.Parse("77efbc01-e542-4c83-837f-2a8bd63f1c82"),
            SessionId = Guid.Parse("a6a3d79c-c06c-4515-b4e7-bf5b1f1a2d98"),
            AutocompleteService = new FakeRemotePathAutocompleteService(
                new RemotePathSuggestion("Downloads", "~/Downloads", isDirectory: true))
        };

        control.SetTextForTest("~/Do");
        await control.RefreshSuggestionsForTestAsync();

        control.SelectSuggestionForTest(0);

        Assert.Equal("~/Downloads/", control.Text);
        Assert.False(control.AreSuggestionsOpenForTest);
    }

    [AvaloniaFact]
    public async Task RemotePathTextBox_TabOnDirectorySuggestion_ReopensSuggestionsForCompletedDirectory()
    {
        var control = new RemotePathTextBox
        {
            ProfileId = Guid.Parse("8ee1fb83-f28a-4903-a3db-23c0f7d05e0f"),
            SessionId = Guid.Parse("102c47f5-5f92-4e87-9fa6-df91d68e77f1"),
            AutocompleteService = new CallbackRemotePathAutocompleteService(input =>
            {
                return input switch
                {
                    "~/Do" => [new RemotePathSuggestion("Downloads", "~/Downloads", isDirectory: true)],
                    "~/Downloads/" => [new RemotePathSuggestion("server", "~/Downloads/server", isDirectory: true)],
                    _ => []
                };
            })
        };
        control.SuggestionDebounceDelay = TimeSpan.Zero;

        control.SetTextForTest("~/Do");
        await control.RefreshSuggestionsForTestAsync();

        await control.AcceptSelectedSuggestionForTestAsync(Avalonia.Input.Key.Tab);

        Assert.Equal("~/Downloads/", control.Text);
        Assert.True(control.AreSuggestionsOpenForTest);
        Assert.Collection(
            control.GetSuggestionsForTest(),
            suggestion => Assert.Equal("server", suggestion.DisplayName));
    }

    [AvaloniaFact]
    public async Task RemotePathTextBox_EnterOnDirectorySuggestion_ClosesSuggestions()
    {
        var control = new RemotePathTextBox
        {
            ProfileId = Guid.Parse("c28bec92-bd88-48e8-9d4b-6434f2ed8dc8"),
            SessionId = Guid.Parse("23f85fa1-8632-4a32-a60c-6f77b4d3b966"),
            AutocompleteService = new FakeRemotePathAutocompleteService(
                new RemotePathSuggestion("Downloads", "~/Downloads", isDirectory: true))
        };

        control.SetTextForTest("~/Do");
        await control.RefreshSuggestionsForTestAsync();

        await control.AcceptSelectedSuggestionForTestAsync(Avalonia.Input.Key.Enter);

        Assert.Equal("~/Downloads/", control.Text);
        Assert.False(control.AreSuggestionsOpenForTest);
    }

    [AvaloniaFact]
    public async Task RemotePathTextBox_TabOnFileSuggestion_CompletesAndClosesSuggestions()
    {
        var control = new RemotePathTextBox
        {
            ProfileId = Guid.Parse("ff0d46f3-ddf8-45d5-89dd-3f462c64f7e1"),
            SessionId = Guid.Parse("7d0548dc-1a7a-4b80-a369-c7fe06f78564"),
            AutocompleteService = new CallbackRemotePathAutocompleteService(input =>
            {
                return input switch
                {
                    "~/mo" => [new RemotePathSuggestion("movie.mkv", "~/movie.mkv", isDirectory: false)],
                    _ => []
                };
            })
        };
        control.SuggestionDebounceDelay = TimeSpan.Zero;

        control.SetTextForTest("~/mo");
        await control.RefreshSuggestionsForTestAsync();

        await control.AcceptSelectedSuggestionForTestAsync(Avalonia.Input.Key.Tab);

        Assert.Equal("~/movie.mkv", control.Text);
        Assert.False(control.AreSuggestionsOpenForTest);
        Assert.Empty(control.GetSuggestionsForTest());
    }

    private sealed class FakeRemotePathAutocompleteService(params RemotePathSuggestion[] suggestions)
        : IRemotePathAutocompleteService
    {
        public Task<IReadOnlyList<RemotePathSuggestion>> GetSuggestionsAsync(
            Guid profileId,
            Guid sessionId,
            string input,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<RemotePathSuggestion>>(suggestions);
        }
    }

    private sealed class CallbackRemotePathAutocompleteService(Func<string, IReadOnlyList<RemotePathSuggestion>> callback)
        : IRemotePathAutocompleteService
    {
        public Task<IReadOnlyList<RemotePathSuggestion>> GetSuggestionsAsync(
            Guid profileId,
            Guid sessionId,
            string input,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(callback(input));
        }
    }
}
