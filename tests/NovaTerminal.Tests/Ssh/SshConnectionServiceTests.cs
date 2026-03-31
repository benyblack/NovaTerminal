using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class SshConnectionServiceTests
{
    [Fact]
    public void SaveProfile_PersistsSshProfileWithoutMutatingTerminalProfiles()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);
            var localProfiles = new List<TerminalProfile>
            {
                new TerminalProfile
                {
                    Id = Guid.Parse("90bcf34a-83af-4e84-b0a3-4a74a9b62615"),
                    Name = "PowerShell",
                    Type = ConnectionType.Local,
                    Command = "pwsh.exe"
                }
            };

            var vm = new NewSshConnectionViewModel
            {
                Name = "Prod",
                HostName = "prod.internal",
                UserName = "alice",
                Port = 2200,
                AuthMode = NewSshAuthMode.Agent
            };

            SshProfile saved = service.SaveProfile(vm);

            Assert.Single(localProfiles);
            Assert.Equal("Prod", saved.Name);
            Assert.Equal("prod.internal", saved.Host);
            Assert.Equal("alice", saved.User);
            Assert.Equal(2200, saved.Port);
            Assert.Equal(SshAuthMode.Agent, saved.AuthMode);

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
    public void SaveProfile_UpdatesExistingStoreProfile()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);

            var existingId = Guid.Parse("5d4bf55d-1b7a-48e9-b368-c03295dcbf1f");
            store.SaveProfile(new SshProfile
            {
                Id = existingId,
                Name = "Old",
                Host = "old.internal",
                Port = 22
            });

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

            SshProfile saved = service.SaveProfile(vm);

            Assert.Equal(existingId, saved.Id);
            Assert.Equal("Updated", saved.Name);
            Assert.Equal("new.internal", saved.Host);
            Assert.Equal("ops", saved.User);
            Assert.Equal(2222, saved.Port);
            Assert.Equal(SshAuthMode.IdentityFile, saved.AuthMode);
            Assert.Equal("C:\\keys\\id_ed25519", saved.IdentityFilePath);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_UpdatesExistingStoreProfile_PreservesBackendKind()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);
            var existingId = Guid.Parse("1183d7c5-e82f-45ce-b8ad-1c343392f822");

            var existing = new SshProfile
            {
                Id = existingId,
                Name = "Native",
                Host = "native.internal",
                Port = 22
            };

            var backendProperty = typeof(SshProfile).GetProperty("BackendKind");
            Assert.NotNull(backendProperty);
            backendProperty!.SetValue(existing, Enum.Parse(backendProperty.PropertyType, "Native"));
            store.SaveProfile(existing);

            var vm = new NewSshConnectionViewModel
            {
                ProfileId = existingId,
                Name = "Native Updated",
                HostName = "updated.internal",
                UserName = "ops",
                Port = 2201
            };

            SshProfile saved = service.SaveProfile(vm);

            Assert.Equal("Native", backendProperty.GetValue(saved)?.ToString());

            SshProfile? persisted = store.GetProfile(existingId);
            Assert.NotNull(persisted);
            Assert.Equal("Native", backendProperty.GetValue(persisted!)?.ToString());
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

            SshProfile saved = service.SaveProfile(vm);
            SshProfile? persisted = store.GetProfile(saved.Id);

            Assert.NotNull(persisted);
            Assert.Equal(10, persisted!.ServerAliveIntervalSeconds);
            Assert.Equal(4, persisted.ServerAliveCountMax);
            Assert.True(persisted.MuxOptions.Enabled);
            Assert.Equal(75, persisted.MuxOptions.ControlPersistSeconds);
            Assert.Equal("-o StrictHostKeyChecking=no", persisted.ExtraSshArgs);
            Assert.Single(persisted.JumpHops);
            Assert.Single(persisted.Forwards);
            Assert.Equal("critical profile", persisted.Notes);
            Assert.Equal("#11AA88", persisted.AccentColor);
            Assert.Contains("favorite", persisted.Tags, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_PersistsRememberPasswordPreferenceForNativeProfiles()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);

            var vm = new NewSshConnectionViewModel
            {
                Name = "Native",
                HostName = "native.internal",
                BackendKind = SshBackendKind.Native,
                RememberPasswordInVault = true
            };

            SshProfile saved = service.SaveProfile(vm);

            Assert.True(saved.RememberPasswordInVault);
            Assert.True(store.GetProfile(saved.Id)!.RememberPasswordInVault);
            Assert.True(new JsonSshProfileStore(path).GetProfile(saved.Id)!.RememberPasswordInVault);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_DropsRememberPasswordPreferenceForOpenSshProfiles()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);

            var vm = new NewSshConnectionViewModel
            {
                Name = "OpenSSH",
                HostName = "openssh.internal",
                RememberPasswordInVault = true
            };

            SshProfile saved = service.SaveProfile(vm);

            Assert.False(saved.RememberPasswordInVault);
            Assert.False(store.GetProfile(saved.Id)!.RememberPasswordInVault);
            Assert.False(new JsonSshProfileStore(path).GetProfile(saved.Id)!.RememberPasswordInVault);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_WithNullBackendPreservesNativeRememberPasswordPreference()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);

            var existing = new SshProfile
            {
                Id = Guid.Parse("b8f6f5c4-1f52-4cc6-8b8b-f8964ad2f1b3"),
                Name = "Native",
                Host = "native.internal",
                BackendKind = SshBackendKind.Native,
                RememberPasswordInVault = true
            };
            store.SaveProfile(existing);

            var vm = new NewSshConnectionViewModel
            {
                ProfileId = existing.Id,
                Name = "Native Updated",
                HostName = "updated.internal",
                UserName = "ops"
            };

            SshProfile saved = service.SaveProfile(vm);

            Assert.Equal(SshBackendKind.Native, saved.BackendKind);
            Assert.True(saved.RememberPasswordInVault);
            Assert.True(store.GetProfile(saved.Id)!.RememberPasswordInVault);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateEditorViewModel_LoadsRememberPasswordPreferenceForNativeProfiles()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);

            var vm = new NewSshConnectionViewModel
            {
                Name = "Native",
                HostName = "native.internal",
                BackendKind = SshBackendKind.Native,
                RememberPasswordInVault = true
            };

            SshProfile saved = service.SaveProfile(vm);
            TerminalProfile runtimeProfile = service.GetConnectionProfile(saved.Id)!;
            NewSshConnectionViewModel editorVm = service.CreateEditorViewModel(runtimeProfile);

            Assert.True(editorVm.RememberPasswordInVault);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveConnectionProfile_ClearsRememberPasswordPreferenceForOpenSshProfiles()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);
            var existing = new SshProfile
            {
                Id = Guid.Parse("e5e221a0-77bf-4f50-a0c3-3136d1f90f9e"),
                Name = "Native",
                Host = "native.internal",
                BackendKind = SshBackendKind.Native,
                RememberPasswordInVault = true
            };

            store.SaveProfile(existing);

            var runtimeProfile = new TerminalProfile
            {
                Id = existing.Id,
                Name = "OpenSSH",
                Type = ConnectionType.SSH,
                SshHost = "openssh.internal",
                SshBackendKind = SshBackendKind.OpenSsh,
                UseSshAgent = true
            };

            service.SaveConnectionProfile(runtimeProfile);

            Assert.False(store.GetProfile(existing.Id)!.RememberPasswordInVault);
            Assert.False(new JsonSshProfileStore(path).GetProfile(existing.Id)!.RememberPasswordInVault);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetConnectionProfiles_ProjectsStoredProfilesIntoRuntimeRows()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);

            var firstId = Guid.Parse("2f2a8e09-bb80-48cc-b386-969ea7b8558f");
            store.SaveProfile(new SshProfile
            {
                Id = firstId,
                Name = "Prod",
                Host = "prod.internal",
                User = "ops",
                Port = 2200,
                Notes = "critical",
                AccentColor = "#66AAFF",
                GroupPath = "Prod/API",
                Tags = new List<string> { "favorite", "api" }
            });

            store.SaveProfile(new SshProfile
            {
                Id = Guid.Parse("f013020a-86d4-4829-a9e5-db06d4cd11dc"),
                Name = "Stage",
                Host = "stage.internal",
                Port = 22,
                GroupPath = "Stage",
                Tags = new List<string> { "staging" }
            });

            IReadOnlyList<TerminalProfile> rows = service.GetConnectionProfiles();

            Assert.Equal(2, rows.Count);
            TerminalProfile prod = rows[0].Id == firstId ? rows[0] : rows[1];
            Assert.Equal(ConnectionType.SSH, prod.Type);
            Assert.Equal("Prod", prod.Name);
            Assert.Equal("prod.internal", prod.SshHost);
            Assert.Equal("ops", prod.SshUser);
            Assert.Equal(2200, prod.SshPort);
            Assert.Equal("critical", prod.Notes);
            Assert.Equal("#66AAFF", prod.AccentColor);
            Assert.Equal("Prod/API", prod.Group);
            Assert.Contains("favorite", prod.Tags, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(OperatingSystem.IsWindows() ? "ssh.exe" : "ssh", prod.Command);
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

    [Fact]
    public void GetConnectionProfiles_ProjectsBackendKindIntoRuntimeProfile()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(path);
            var service = new SshConnectionService(store);
            var profile = new SshProfile
            {
                Id = Guid.Parse("5f3d3cab-3cda-442a-aec1-a983b3eb8d1b"),
                Name = "Native",
                Host = "native.internal"
            };

            var backendProperty = typeof(SshProfile).GetProperty("BackendKind");
            Assert.NotNull(backendProperty);
            backendProperty!.SetValue(profile, Enum.Parse(backendProperty.PropertyType, "Native"));
            store.SaveProfile(profile);

            TerminalProfile runtime = service.GetConnectionProfiles().Single();
            var runtimeBackendProperty = typeof(TerminalProfile).GetProperty("SshBackendKind");

            Assert.NotNull(runtimeBackendProperty);
            Assert.Equal("Native", runtimeBackendProperty!.GetValue(runtime)?.ToString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
