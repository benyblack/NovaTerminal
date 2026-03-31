using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;
using System.Reflection;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class SshSessionFactoryTests
{
    [Fact]
    public void Create_ForNativeProfileRoutesToNativeSessionStub()
    {
        var backendProperty = typeof(SshProfile).GetProperty("BackendKind");
        Assert.NotNull(backendProperty);

        var profileId = Guid.Parse("e94d09da-1269-4ecf-86b2-81bd4ec483cc");
        var store = new InMemorySshProfileStore(new SshProfile
        {
            Id = profileId,
            Name = "native",
            Host = "native.internal"
        });
        backendProperty!.SetValue(store.Profile, Enum.Parse(backendProperty.PropertyType, "Native"));

        var factoryType = typeof(SshProfile).Assembly.GetType("NovaTerminal.Core.Ssh.Sessions.SshSessionFactory");
        Assert.NotNull(factoryType);

        object? factory = Activator.CreateInstance(factoryType!, store);
        Assert.NotNull(factory);

        var createMethod = factoryType!.GetMethod(
            "Create",
            new[]
            {
                typeof(Guid),
                typeof(int),
                typeof(int),
                typeof(SshDiagnosticsLevel),
                typeof(IReadOnlyList<string>),
                typeof(Action<string>)
            });

        Assert.NotNull(createMethod);

        var exception = Record.Exception(() => createMethod!.Invoke(
            factory,
            new object?[] { profileId, 120, 30, SshDiagnosticsLevel.None, null, null }));

        Assert.NotNull(exception);
        var actual = exception is TargetInvocationException tie && tie.InnerException != null
            ? tie.InnerException
            : exception;
        Assert.IsType<NotSupportedException>(actual);
        Assert.Contains("Native SSH", actual.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InMemorySshProfileStore : ISshProfileStore
    {
        public InMemorySshProfileStore(SshProfile profile)
        {
            Profile = profile;
        }

        public SshProfile Profile { get; }

        public IReadOnlyList<SshProfile> GetProfiles() => new[] { Profile };

        public SshProfile? GetProfile(Guid profileId) => Profile.Id == profileId ? Profile : null;

        public void SaveProfile(SshProfile profile) => throw new NotSupportedException();

        public bool DeleteProfile(Guid profileId) => throw new NotSupportedException();
    }
}
