using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Storage;

public interface ISshProfileStore
{
    IReadOnlyList<SshProfile> GetProfiles();
    SshProfile? GetProfile(Guid profileId);
    void SaveProfile(SshProfile profile);
    bool DeleteProfile(Guid profileId);
}

public sealed class SshProfileStoreSnapshot
{
    public int SchemaVersion { get; init; }
    public List<SshProfile> Profiles { get; init; } = new();
}

public interface ISshProfileStoreMigration
{
    int SourceSchemaVersion { get; }
    int TargetSchemaVersion { get; }

    SshProfileStoreSnapshot Migrate(SshProfileStoreSnapshot source);
}
