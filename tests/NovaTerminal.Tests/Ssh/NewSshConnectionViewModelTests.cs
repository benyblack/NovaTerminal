using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class NewSshConnectionViewModelTests
{
    [Fact]
    public void Validate_RequiresHostName()
    {
        var vm = new NewSshConnectionViewModel
        {
            Name = "Prod",
            HostName = "   "
        };

        bool valid = vm.Validate();

        Assert.False(valid);
        Assert.Equal("Host name is required.", vm.ValidationError);
    }

    [Fact]
    public void ToSshProfile_UsesDeterministicNormalization()
    {
        var id = Guid.Parse("f413b5c1-2fef-43ba-b8f3-534b6fd201da");
        var vm = new NewSshConnectionViewModel
        {
            ProfileId = id,
            Name = "  Production  ",
            HostName = "  host.internal  ",
            UserName = "  devops  ",
            Port = 2222,
            AuthMode = NewSshAuthMode.IdentityFile,
            IdentityFilePath = "  C:\\keys\\prod_key  "
        };

        SshProfile first = vm.ToSshProfile();
        SshProfile second = vm.ToSshProfile();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Host, second.Host);
        Assert.Equal(first.User, second.User);
        Assert.Equal(first.Port, second.Port);
        Assert.Equal(first.AuthMode, second.AuthMode);
        Assert.Equal(first.IdentityFilePath, second.IdentityFilePath);

        Assert.Equal(id, first.Id);
        Assert.Equal("Production", first.Name);
        Assert.Equal("host.internal", first.Host);
        Assert.Equal("devops", first.User);
        Assert.Equal(2222, first.Port);
        Assert.Equal(SshAuthMode.IdentityFile, first.AuthMode);
        Assert.Equal("C:\\keys\\prod_key", first.IdentityFilePath);
    }

    [Fact]
    public void Validate_IdentityFileMissingOnDisk_EmitsWarningButStaysValid()
    {
        var vm = new NewSshConnectionViewModel
        {
            Name = "Prod",
            HostName = "host.internal",
            AuthMode = NewSshAuthMode.IdentityFile,
            IdentityFilePath = Path.Combine(Path.GetTempPath(), $"missing_key_{Guid.NewGuid():N}")
        };

        bool valid = vm.Validate();

        Assert.True(valid);
        Assert.Equal(string.Empty, vm.ValidationError);
        Assert.Contains("does not exist", vm.ValidationWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToSshProfile_IncludesAdvancedEditorFields()
    {
        var id = Guid.Parse("f8097442-2e68-47e4-9f51-42f345f4f0a7");
        var vm = new NewSshConnectionViewModel
        {
            ProfileId = id,
            Name = "Advanced",
            HostName = "advanced.internal",
            UserName = "ops",
            Port = 2200,
            KeepAliveIntervalSeconds = 20,
            KeepAliveCountMax = 6,
            EnableMux = true,
            ControlPersistSeconds = 120,
            ExtraSshArgs = " -o StrictHostKeyChecking=no "
        };

        vm.JumpHops.Add(new SshJumpHop { Host = "jump-1.internal", Port = 22 });
        vm.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Local,
            BindAddress = "127.0.0.1",
            SourcePort = 15432,
            DestinationHost = "db.internal",
            DestinationPort = 5432
        });

        SshProfile profile = vm.ToSshProfile();

        Assert.Equal(20, profile.ServerAliveIntervalSeconds);
        Assert.Equal(6, profile.ServerAliveCountMax);
        Assert.True(profile.MuxOptions.Enabled);
        Assert.Equal(120, profile.MuxOptions.ControlPersistSeconds);
        Assert.Equal("-o StrictHostKeyChecking=no", profile.ExtraSshArgs);
        Assert.Single(profile.JumpHops);
        Assert.Single(profile.Forwards);
    }

    [Fact]
    public void ToSshProfile_PreservesZeroControlPersistWhenMuxEnabled()
    {
        var vm = new NewSshConnectionViewModel
        {
            Name = "ZeroPersist",
            HostName = "zero.internal",
            EnableMux = true,
            ControlPersistSeconds = 0
        };

        SshProfile profile = vm.ToSshProfile();

        Assert.True(profile.MuxOptions.Enabled);
        Assert.Equal(0, profile.MuxOptions.ControlPersistSeconds);
    }

    [Fact]
    public void ApplySshProfile_PreservesZeroControlPersistValue()
    {
        var vm = new NewSshConnectionViewModel();
        var profile = new SshProfile
        {
            Name = "ZeroPersist",
            Host = "zero.internal",
            MuxOptions = new SshMuxOptions
            {
                Enabled = true,
                ControlPersistSeconds = 0
            }
        };

        vm.ApplySshProfile(profile);

        Assert.Equal(0, vm.ControlPersistSeconds);
    }

    [Fact]
    public void ApplySshProfile_RoundTripsBackendKind()
    {
        var vm = new NewSshConnectionViewModel();
        var profile = new SshProfile
        {
            Name = "Native",
            Host = "native.internal"
        };

        var backendProperty = typeof(SshProfile).GetProperty("BackendKind");
        Assert.NotNull(backendProperty);
        backendProperty!.SetValue(profile, Enum.Parse(backendProperty.PropertyType, "Native"));

        var viewModelBackendProperty = typeof(NewSshConnectionViewModel).GetProperty("BackendKind");
        Assert.NotNull(viewModelBackendProperty);

        vm.ApplySshProfile(profile);

        Assert.Equal("Native", viewModelBackendProperty!.GetValue(vm)?.ToString());

        SshProfile roundTripped = vm.ToSshProfile();
        Assert.Equal("Native", backendProperty.GetValue(roundTripped)?.ToString());
    }

    [Fact]
    public void BackendWarning_WhenNativeSelectedAndExperimentalToggleDisabled_IsVisible()
    {
        var vm = new NewSshConnectionViewModel
        {
            HostName = "native.internal",
            ExperimentalNativeSshEnabled = false,
            BackendKind = SshBackendKind.Native
        };

        Assert.Contains("disabled globally", vm.BackendWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackendWarning_WhenNativeSelectedAndExperimentalToggleEnabled_IsCleared()
    {
        var vm = new NewSshConnectionViewModel
        {
            HostName = "native.internal",
            ExperimentalNativeSshEnabled = true,
            BackendKind = SshBackendKind.Native
        };

        Assert.Equal(string.Empty, vm.BackendWarning);
    }
}
