using NovaTerminal.Core;
using System.Reflection;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    public class EmojiShapingDetectionTests
    {
        [Fact]
        public void RegionalIndicatorFlagSequence_RequiresComplexShaping()
        {
            MethodInfo? method = typeof(TerminalDrawOperation).GetMethod(
                "ContainsRunesRequiringComplexShaping",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            bool requires = (bool)(method!.Invoke(null, new object[] { "🇺🇸" }) ?? false);
            Assert.True(requires);
        }
    }
}
