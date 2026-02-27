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
}
