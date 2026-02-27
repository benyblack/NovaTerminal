using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class SshConnectionServiceTests
{
    [Fact]
    public void SaveProfile_AddsTerminalProfileAndPersistsSshProfile()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);
            var profiles = new List<TerminalProfile>();

            var vm = new NewSshConnectionViewModel
            {
                Name = "Prod",
                HostName = "prod.internal",
                UserName = "alice",
                Port = 2200,
                AuthMode = NewSshAuthMode.Agent
            };

            TerminalProfile saved = service.SaveProfile(vm, profiles);

            Assert.Single(profiles);
            Assert.Equal(ConnectionType.SSH, saved.Type);
            Assert.Equal("Prod", saved.Name);
            Assert.Equal("prod.internal", saved.SshHost);
            Assert.Equal("alice", saved.SshUser);
            Assert.Equal(2200, saved.SshPort);
            Assert.True(saved.UseSshAgent);

            var persisted = store.GetProfile(saved.Id);
            Assert.NotNull(persisted);
            Assert.Equal("prod.internal", persisted!.Host);
            Assert.Equal("alice", persisted.User);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_UpdatesExistingTerminalProfile()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);

            var existingId = Guid.Parse("5d4bf55d-1b7a-48e9-b368-c03295dcbf1f");
            var profiles = new List<TerminalProfile>
            {
                new TerminalProfile
                {
                    Id = existingId,
                    Name = "Old",
                    Type = ConnectionType.SSH,
                    SshHost = "old.internal",
                    SshPort = 22
                }
            };

            var vm = new NewSshConnectionViewModel
            {
                ProfileId = existingId,
                Name = "Updated",
                HostName = "new.internal",
                UserName = "ops",
                Port = 2222,
                AuthMode = NewSshAuthMode.IdentityFile,
                IdentityFilePath = "C:\\keys\\id_ed25519"
            };

            TerminalProfile saved = service.SaveProfile(vm, profiles);

            Assert.Single(profiles);
            Assert.Equal(existingId, saved.Id);
            Assert.Equal("Updated", saved.Name);
            Assert.Equal("new.internal", saved.SshHost);
            Assert.Equal("ops", saved.SshUser);
            Assert.Equal(2222, saved.SshPort);
            Assert.False(saved.UseSshAgent);
            Assert.Equal("C:\\keys\\id_ed25519", saved.IdentityFilePath);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_PersistsAdvancedSshOptionsAndFavoriteMetadata()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);
            var profiles = new List<TerminalProfile>();

            var vm = new NewSshConnectionViewModel
            {
                Name = "Advanced",
                HostName = "advanced.internal",
                UserName = "dba",
                Port = 2201,
                AuthMode = NewSshAuthMode.Agent,
                IsFavorite = true,
                Notes = "critical profile",
                AccentColor = "#11AA88",
                KeepAliveIntervalSeconds = 10,
                KeepAliveCountMax = 4,
                EnableMux = true,
                ControlPersistSeconds = 75,
                ExtraSshArgs = "-o StrictHostKeyChecking=no"
            };

            vm.JumpHops.Add(new SshJumpHop { Host = "jump.internal", User = "ops", Port = 22 });
            vm.Forwards.Add(new PortForward
            {
                Kind = PortForwardKind.Dynamic,
                BindAddress = "127.0.0.1",
                SourcePort = 1080
            });

            TerminalProfile saved = service.SaveProfile(vm, profiles);
            SshProfile? persisted = store.GetProfile(saved.Id);

            Assert.NotNull(persisted);
            Assert.Equal(10, persisted!.ServerAliveIntervalSeconds);
            Assert.Equal(4, persisted.ServerAliveCountMax);
            Assert.True(persisted.MuxOptions.Enabled);
            Assert.Equal(75, persisted.MuxOptions.ControlPersistSeconds);
            Assert.Equal("-o StrictHostKeyChecking=no", persisted.ExtraSshArgs);
            Assert.Single(persisted.JumpHops);
            Assert.Single(persisted.Forwards);

            Assert.Contains("favorite", saved.Tags, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("critical profile", saved.Notes);
            Assert.Equal("#11AA88", saved.AccentColor);
            Assert.Single(saved.Forwards);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_service_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
