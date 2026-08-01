using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

public sealed class CommandAssistModeRouter
{
    private const double FixModeThreshold = 0.8;

    public CommandAssistMode ChooseModeForHelpRequest()
    {
        return CommandAssistMode.Help;
    }

    public CommandAssistMode ChooseModeForFailure(double highestConfidence)
    {
        return highestConfidence >= FixModeThreshold
            ? CommandAssistMode.Fix
            : CommandAssistMode.Suggest;
    }
}
