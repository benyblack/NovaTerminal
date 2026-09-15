using System.Text.Json;
using NovaTerminal.Shell;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// In the Architecture project rather than beside the rest of the app tests for the same reason
/// as <see cref="UpdateCoordinatorTests"/>: this project is in the gating unit loop that
/// <c>ci.yml</c> and <c>release.yml</c> run, and <c>App.Tests</c> is not (CI marks it green via
/// <c>continue-on-error</c>). These are pure serialization assertions with no Avalonia, Windows
/// or network dependency, and the regression they pin - an existing user's settings file
/// silently opting them out of update checks - is precisely the kind that must not be able to
/// ship on a green build.
/// </summary>
public class UpdateSettingsTests
{
    [Fact]
    public void Automatic_update_checks_default_to_on()
    {
        Assert.True(new TerminalSettings().AutomaticUpdateChecks);
    }

    [Fact]
    public void Automatic_update_checks_survive_a_json_round_trip()
    {
        var settings = new TerminalSettings { AutomaticUpdateChecks = false };

        var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
        var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings);

        Assert.NotNull(restored);
        Assert.False(restored!.AutomaticUpdateChecks);
    }

    [Fact]
    public void A_settings_file_written_before_this_setting_existed_opts_in()
    {
        // Users upgrading from a build without the property must land on the default rather
        // than on `false`, which is what a bare `default(bool)` would give them.
        var restored = JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings);

        Assert.NotNull(restored);
        Assert.True(restored!.AutomaticUpdateChecks);
    }
}
