using System.Text.Json;
using NovaTerminal.Shell;

namespace NovaTerminal.AppTests.Update;

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
