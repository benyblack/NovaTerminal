using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Tests.Core;

public sealed class SessionManagerTests
{
    [AvaloniaFact]
    public void RestoreSession_UsesStoreBackedSshProfileAndPreservesBackendKind()
    {
        string storePath = JsonSshProfileStore.GetDefaultStorePath();
        string storeDirectory = Path.GetDirectoryName(storePath)!;
        string backupPath = storePath + ".task5-test-backup";
        bool hadExisting = File.Exists(storePath);

        Directory.CreateDirectory(storeDirectory);
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        if (hadExisting)
        {
            File.Copy(storePath, backupPath, overwrite: true);
        }

        try
        {
            var store = new JsonSshProfileStore(storePath);
            Guid sshId = Guid.Parse("4bcf6934-c30d-4100-99e8-a9b5a283fc5d");
            store.SaveProfile(new SshProfile
            {
                Id = sshId,
                Name = "Native SSH",
                Host = "native.internal",
                User = "ops",
                Port = 22,
                BackendKind = SshBackendKind.Native
            });

            var session = new NovaSession
            {
                Tabs =
                {
                    new TabSession
                    {
                        Title = "Native SSH",
                        Root = new PaneNode
                        {
                            Type = NodeType.Leaf,
                            ProfileId = sshId.ToString(),
                            SshProfileId = sshId.ToString(),
                            PaneId = Guid.NewGuid().ToString()
                        }
                    }
                }
            };

            var tabs = new TabControl();
            var window = new Window();
            var settings = new TerminalSettings
            {
                Profiles = new List<TerminalProfile>
                {
                    new TerminalProfile
                    {
                        Id = Guid.Parse("6f9c6f43-f1e8-4873-ac64-08ae12722b9d"),
                        Name = "Local",
                        Type = ConnectionType.Local,
                        Command = "pwsh.exe"
                    }
                }
            };
            settings.DefaultProfileId = settings.Profiles[0].Id;

            SessionManager.RestoreSession(window, tabs, settings, session);

            var tab = Assert.IsType<TabItem>(Assert.Single(tabs.Items));
            var pane = Assert.IsType<TerminalPane>(tab.Content);
            Assert.NotNull(pane.Profile);
            Assert.Equal(ConnectionType.SSH, pane.Profile!.Type);
            Assert.Equal(SshBackendKind.Native, pane.Profile.SshBackendKind);
        }
        finally
        {
            if (hadExisting)
            {
                File.Copy(backupPath, storePath, overwrite: true);
                File.Delete(backupPath);
            }
            else if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }
}
