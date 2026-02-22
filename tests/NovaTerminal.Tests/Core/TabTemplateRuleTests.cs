using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class TabTemplateRuleTests
{
    [Fact]
    public void FindTabTemplateRule_ReturnsEnabledMatchingRule()
    {
        Guid profileId = Guid.NewGuid();
        var rules = new List<TabTemplateRule>
        {
            new() { ProfileId = Guid.NewGuid(), TemplateName = "other", Enabled = true },
            new() { ProfileId = profileId, TemplateName = "dev-template", Enabled = true }
        };

        var rule = NovaTerminal.MainWindow.FindTabTemplateRule(rules, profileId);
        Assert.NotNull(rule);
        Assert.Equal("dev-template", rule!.TemplateName);
    }

    [Fact]
    public void FindTabTemplateRule_IgnoresDisabledOrEmptyRules()
    {
        Guid profileId = Guid.NewGuid();
        var rules = new List<TabTemplateRule>
        {
            new() { ProfileId = profileId, TemplateName = "", Enabled = true },
            new() { ProfileId = profileId, TemplateName = "x", Enabled = false }
        };

        var rule = NovaTerminal.MainWindow.FindTabTemplateRule(rules, profileId);
        Assert.Null(rule);
    }

    [Fact]
    public void UpsertTabTemplateRule_AddsAndUpdates()
    {
        Guid profileId = Guid.NewGuid();
        var rules = new List<TabTemplateRule>();

        bool added = NovaTerminal.MainWindow.UpsertTabTemplateRule(rules, profileId, "first");
        Assert.True(added);
        Assert.Single(rules);
        Assert.Equal("first", rules[0].TemplateName);

        bool updated = NovaTerminal.MainWindow.UpsertTabTemplateRule(rules, profileId, "second");
        Assert.True(updated);
        Assert.Single(rules);
        Assert.Equal("second", rules[0].TemplateName);
        Assert.True(rules[0].Enabled);
    }

    [Fact]
    public void RemoveTabTemplateRule_RemovesMatchingRules()
    {
        Guid profileId = Guid.NewGuid();
        var rules = new List<TabTemplateRule>
        {
            new() { ProfileId = profileId, TemplateName = "x", Enabled = true },
            new() { ProfileId = Guid.NewGuid(), TemplateName = "y", Enabled = true }
        };

        bool removed = NovaTerminal.MainWindow.RemoveTabTemplateRule(rules, profileId);
        Assert.True(removed);
        Assert.Single(rules);
        Assert.DoesNotContain(rules, r => r.ProfileId == profileId);
    }
}
