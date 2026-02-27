using NovaTerminal.Core;
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

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_service_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
