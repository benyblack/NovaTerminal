using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class SshArgBuilderTests
{
    [Fact]
    public void Build_BasicProfile_ProducesExpectedArgs()
    {
        var profile = new SshProfile
        {
            Id = Guid.Parse("531619ce-5685-4f74-9538-daf8a194ec39"),
            Name = "basic",
            Host = "example.com",
            User = "alice",
            Port = 22
        };

        string args = SshArgBuilder.BuildCommandLine(profile);

        Assert.Equal("alice@example.com", args);
    }

    [Fact]
    public void Build_WithJumpHosts_ProducesProxyJumpChain()
    {
        var profile = new SshProfile
        {
            Id = Guid.Parse("4ec67053-9ab9-4b9a-8124-b0887f191d42"),
            Name = "jumped",
            Host = "target.internal",
            User = "dev",
            JumpHops =
            {
                new SshJumpHop { Host = "jump-one.internal" },
                new SshJumpHop { Host = "jump-two.internal", User = "ops", Port = 2222 }
            }
        };

        string args = SshArgBuilder.BuildCommandLine(profile);

        Assert.Equal("-J jump-one.internal,ops@jump-two.internal:2222 dev@target.internal", args);
    }

    [Fact]
    public void Build_WithLocalForward_ProducesLFlag()
    {
        var profile = new SshProfile
        {
            Name = "local-fwd",
            Host = "target.internal",
            Forwards =
            {
                new PortForward
                {
                    Kind = PortForwardKind.Local,
                    BindAddress = "127.0.0.1",
                    SourcePort = 8080,
                    DestinationHost = "db.internal",
                    DestinationPort = 80
                }
            }
        };

        string args = SshArgBuilder.BuildCommandLine(profile);

        Assert.Equal("-L 127.0.0.1:8080:db.internal:80 target.internal", args);
    }

    [Fact]
    public void Build_WithRemoteForward_ProducesRFlag()
    {
        var profile = new SshProfile
        {
            Name = "remote-fwd",
            Host = "target.internal",
            Forwards =
            {
                new PortForward
                {
                    Kind = PortForwardKind.Remote,
                    BindAddress = "0.0.0.0",
                    SourcePort = 2022,
                    DestinationHost = "127.0.0.1",
                    DestinationPort = 22
                }
            }
        };

        string args = SshArgBuilder.BuildCommandLine(profile);

        Assert.Equal("-R 0.0.0.0:2022:127.0.0.1:22 target.internal", args);
    }

    [Fact]
    public void Build_WithDynamicForward_ProducesDFlag()
    {
        var profile = new SshProfile
        {
            Name = "dynamic-fwd",
            Host = "target.internal",
            Forwards =
            {
                new PortForward
                {
                    Kind = PortForwardKind.Dynamic,
                    BindAddress = "127.0.0.1",
                    SourcePort = 1080
                }
            }
        };

        string args = SshArgBuilder.BuildCommandLine(profile);

        Assert.Equal("-D 127.0.0.1:1080 target.internal", args);
    }
}
