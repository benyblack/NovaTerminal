using Xunit;

namespace Ntilde.Platform.Tests.Infra;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!IsEnabled())
        {
            Skip = "NTILDE_ENABLE_DOCKER_E2E is not set to 1. Skipping optional Docker end-to-end test.";
        }
    }

    private static bool IsEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("NTILDE_ENABLE_DOCKER_E2E");
        return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }
}
