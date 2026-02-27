using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.OpenSsh;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class OpenSshConfigCompilerTests
{
    [Fact]
    public void Compile_BasicProfile_EmitsStableAliasAndHostBlock()
    {
        string root = CreateTempDirectory();
        try
        {
            var compiler = new OpenSshConfigCompiler(root);
            var profile = new SshProfile
            {
                Id = Guid.Parse("65e4a32f-9817-4b89-b607-6f3472580e81"),
                Name = "basic",
                Host = "example.com",
                User = "alice",
                Port = 2222
            };

            OpenSshCompilationResult result = compiler.Compile(new[] { profile }, profile.Id);
            string text = File.ReadAllText(result.ConfigFilePath);

            Assert.Equal("nova_65e4a32f98174b89b6076f3472580e81", result.Alias);
            Assert.Contains("Host nova_65e4a32f98174b89b6076f3472580e81", text);
            Assert.Contains("HostName example.com", text);
            Assert.Contains("User alice", text);
            Assert.Contains("Port 2222", text);
            Assert.Contains("ServerAliveInterval 30", text);
            Assert.Contains("ServerAliveCountMax 3", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Compile_WithProxyJumpAndForwards_EmitsExpectedDirectives()
    {
        string root = CreateTempDirectory();
        try
        {
            var compiler = new OpenSshConfigCompiler(root);
            var profile = new SshProfile
            {
                Id = Guid.Parse("4aab0508-34f8-4e4d-9023-ac8be7069755"),
                Name = "complex",
                Host = "target.internal",
                JumpHops =
                {
                    new SshJumpHop { Host = "jump-1.internal" },
                    new SshJumpHop { Host = "jump-2.internal", User = "ops", Port = 2200 }
                },
                Forwards =
                {
                    new PortForward
                    {
                        Kind = PortForwardKind.Local,
                        BindAddress = "127.0.0.1",
                        SourcePort = 8080,
                        DestinationHost = "localhost",
                        DestinationPort = 80
                    },
                    new PortForward
                    {
                        Kind = PortForwardKind.Remote,
                        BindAddress = "0.0.0.0",
                        SourcePort = 2022,
                        DestinationHost = "127.0.0.1",
                        DestinationPort = 22
                    },
                    new PortForward
                    {
                        Kind = PortForwardKind.Dynamic,
                        BindAddress = "127.0.0.1",
                        SourcePort = 1080
                    }
                }
            };

            OpenSshCompilationResult result = compiler.Compile(new[] { profile }, profile.Id);
            string text = File.ReadAllText(result.ConfigFilePath);

            Assert.Contains("ProxyJump jump-1.internal,ops@jump-2.internal:2200", text);
            Assert.Contains("LocalForward 127.0.0.1:8080 localhost:80", text);
            Assert.Contains("RemoteForward 0.0.0.0:2022 127.0.0.1:22", text);
            Assert.Contains("DynamicForward 127.0.0.1:1080", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Compile_WithMuxEnabled_EmitsControlMasterOptions()
    {
        string root = CreateTempDirectory();
        try
        {
            var compiler = new OpenSshConfigCompiler(root);
            var profile = new SshProfile
            {
                Id = Guid.Parse("f8f19f37-da64-4de3-8c4c-7f9227eaaf20"),
                Host = "example.com",
                MuxOptions = new SshMuxOptions
                {
                    Enabled = true,
                    ControlMasterAuto = true,
                    ControlPersistSeconds = 90
                }
            };

            OpenSshCompilationResult result = compiler.Compile(new[] { profile }, profile.Id);
            string text = File.ReadAllText(result.ConfigFilePath);

            Assert.Contains("ControlMaster auto", text);
            Assert.Contains("ControlPersist 90", text);
            Assert.Contains("ControlPath ", text);
            Assert.Contains("cm_f8f19f37da644de38c4c7f9227eaaf20", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Compile_WritesAtomically_WithoutTempFileLeftBehind()
    {
        string root = CreateTempDirectory();
        try
        {
            var compiler = new OpenSshConfigCompiler(root);
            var profile = new SshProfile
            {
                Id = Guid.Parse("685f8a5f-6b73-4660-ad8c-59ad13e6cc30"),
                Host = "example.com"
            };

            OpenSshCompilationResult result = compiler.Compile(new[] { profile }, profile.Id);

            Assert.True(File.Exists(result.ConfigFilePath));

            string[] tempFiles = Directory.GetFiles(Path.GetDirectoryName(result.ConfigFilePath)!, "*.tmp");
            Assert.Empty(tempFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Compile_SameInput_ProducesDeterministicOutput()
    {
        string root = CreateTempDirectory();
        try
        {
            var compiler = new OpenSshConfigCompiler(root);
            var profile = new SshProfile
            {
                Id = Guid.Parse("4f9a7447-ff5d-42b2-86fa-e4169dd4d335"),
                Host = "stable.internal",
                User = "svc"
            };

            OpenSshCompilationResult firstResult = compiler.Compile(new[] { profile }, profile.Id);
            string firstText = File.ReadAllText(firstResult.ConfigFilePath);

            OpenSshCompilationResult secondResult = compiler.Compile(new[] { profile }, profile.Id);
            string secondText = File.ReadAllText(secondResult.ConfigFilePath);

            Assert.Equal(firstText, secondText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_compiler_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
