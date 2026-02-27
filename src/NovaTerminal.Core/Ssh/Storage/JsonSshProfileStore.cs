using System.Text.Json;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Storage;

public sealed class JsonSshProfileStore : ISshProfileStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _storeFilePath;
    private readonly Dictionary<int, ISshProfileStoreMigration> _migrations;

    public JsonSshProfileStore(string? storeFilePath = null, IEnumerable<ISshProfileStoreMigration>? migrations = null)
    {
        _storeFilePath = string.IsNullOrWhiteSpace(storeFilePath)
            ? GetDefaultStorePath()
            : Path.GetFullPath(storeFilePath);

        _migrations = (migrations ?? Array.Empty<ISshProfileStoreMigration>())
            .GroupBy(m => m.SourceSchemaVersion)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public string StoreFilePath => _storeFilePath;

    public static string GetDefaultStorePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "NovaTerminal", "ssh", "profiles.json");
    }

    public IReadOnlyList<SshProfile> GetProfiles()
    {
        lock (_syncRoot)
        {
            var document = LoadDocumentLocked();
            return document.Profiles
                .OrderBy(profile => profile.Id)
                .Select(CloneProfile)
                .ToArray();
        }
    }

    public SshProfile? GetProfile(Guid profileId)
    {
        lock (_syncRoot)
        {
            var document = LoadDocumentLocked();
            var profile = document.Profiles.FirstOrDefault(existing => existing.Id == profileId);
            return profile is null ? null : CloneProfile(profile);
        }
    }

    public void SaveProfile(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_syncRoot)
        {
            var document = LoadDocumentLocked();
            var normalized = NormalizeProfile(profile);
            int existingIndex = document.Profiles.FindIndex(existing => existing.Id == normalized.Id);

            if (existingIndex >= 0)
            {
                document.Profiles[existingIndex] = normalized;
            }
            else
            {
                document.Profiles.Add(normalized);
            }

            PersistDocumentLocked(document);
        }
    }

    public bool DeleteProfile(Guid profileId)
    {
        lock (_syncRoot)
        {
            var document = LoadDocumentLocked();
            bool removed = document.Profiles.RemoveAll(profile => profile.Id == profileId) > 0;

            if (removed)
            {
                PersistDocumentLocked(document);
            }

            return removed;
        }
    }

    private StoreDocument LoadDocumentLocked()
    {
        if (!File.Exists(_storeFilePath))
        {
            return CreateNewDocument();
        }

        try
        {
            string json = File.ReadAllText(_storeFilePath);
            var document = JsonSerializer.Deserialize<StoreDocument>(json, JsonOptions) ?? CreateNewDocument();
            document.Profiles ??= new List<SshProfile>();

            if (document.SchemaVersion <= 0)
            {
                document.SchemaVersion = CurrentSchemaVersion;
            }
            else if (document.SchemaVersion < CurrentSchemaVersion)
            {
                document = ApplyMigrationsLocked(document);
            }

            SortProfiles(document.Profiles);
            return document;
        }
        catch
        {
            return CreateNewDocument();
        }
    }

    private StoreDocument ApplyMigrationsLocked(StoreDocument document)
    {
        int guard = 0;
        var snapshot = ToSnapshot(document);

        while (snapshot.SchemaVersion < CurrentSchemaVersion && guard < 16)
        {
            if (!_migrations.TryGetValue(snapshot.SchemaVersion, out ISshProfileStoreMigration? migration))
            {
                break;
            }

            snapshot = migration.Migrate(snapshot);
            guard++;
        }

        if (snapshot.SchemaVersion < CurrentSchemaVersion)
        {
            snapshot = new SshProfileStoreSnapshot
            {
                SchemaVersion = CurrentSchemaVersion,
                Profiles = snapshot.Profiles.Select(CloneProfile).ToList()
            };
        }

        return FromSnapshot(snapshot);
    }

    private void PersistDocumentLocked(StoreDocument document)
    {
        document.SchemaVersion = CurrentSchemaVersion;
        SortProfiles(document.Profiles);

        string? directory = Path.GetDirectoryName(_storeFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(_storeFilePath, json);
    }

    private static void SortProfiles(List<SshProfile> profiles)
    {
        profiles.Sort((left, right) => left.Id.CompareTo(right.Id));
    }

    private static StoreDocument CreateNewDocument()
    {
        return new StoreDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Profiles = new List<SshProfile>()
        };
    }

    private static SshProfile NormalizeProfile(SshProfile profile)
    {
        var normalized = CloneProfile(profile);
        normalized.Name = normalized.Name?.Trim() ?? string.Empty;
        normalized.Host = normalized.Host?.Trim() ?? string.Empty;
        normalized.User = normalized.User?.Trim() ?? string.Empty;
        normalized.Port = normalized.Port > 0 ? normalized.Port : 22;
        normalized.IdentityFilePath = normalized.IdentityFilePath?.Trim() ?? string.Empty;
        normalized.WorkingDirectory = normalized.WorkingDirectory?.Trim() ?? string.Empty;
        normalized.JumpHops = normalized.JumpHops.Select(NormalizeJumpHop).ToList();
        normalized.Forwards = normalized.Forwards.Select(NormalizeForward).ToList();
        normalized.MuxOptions = NormalizeMuxOptions(normalized.MuxOptions);

        if (string.IsNullOrWhiteSpace(normalized.Host))
        {
            throw new ArgumentException("SSH profile host cannot be empty.", nameof(profile));
        }

        return normalized;
    }

    private static SshJumpHop NormalizeJumpHop(SshJumpHop hop)
    {
        return new SshJumpHop
        {
            Host = hop.Host?.Trim() ?? string.Empty,
            User = hop.User?.Trim() ?? string.Empty,
            Port = hop.Port > 0 ? hop.Port : 22
        };
    }

    private static PortForward NormalizeForward(PortForward forward)
    {
        return new PortForward
        {
            Kind = forward.Kind,
            BindAddress = forward.BindAddress?.Trim() ?? string.Empty,
            SourcePort = forward.SourcePort,
            DestinationHost = forward.DestinationHost?.Trim() ?? string.Empty,
            DestinationPort = forward.DestinationPort
        };
    }

    private static SshMuxOptions NormalizeMuxOptions(SshMuxOptions? mux)
    {
        mux ??= new SshMuxOptions();
        return new SshMuxOptions
        {
            Enabled = mux.Enabled,
            ControlMasterAuto = mux.ControlMasterAuto,
            ControlPath = mux.ControlPath?.Trim() ?? string.Empty,
            ControlPersistSeconds = mux.ControlPersistSeconds
        };
    }

    private static SshProfile CloneProfile(SshProfile profile)
    {
        return new SshProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Host = profile.Host,
            User = profile.User,
            Port = profile.Port,
            AuthMode = profile.AuthMode,
            IdentityFilePath = profile.IdentityFilePath,
            JumpHops = profile.JumpHops.Select(CloneJumpHop).ToList(),
            Forwards = profile.Forwards.Select(CloneForward).ToList(),
            MuxOptions = CloneMuxOptions(profile.MuxOptions),
            WorkingDirectory = profile.WorkingDirectory
        };
    }

    private static SshJumpHop CloneJumpHop(SshJumpHop hop)
    {
        return new SshJumpHop
        {
            Host = hop.Host,
            User = hop.User,
            Port = hop.Port
        };
    }

    private static PortForward CloneForward(PortForward forward)
    {
        return new PortForward
        {
            Kind = forward.Kind,
            BindAddress = forward.BindAddress,
            SourcePort = forward.SourcePort,
            DestinationHost = forward.DestinationHost,
            DestinationPort = forward.DestinationPort
        };
    }

    private static SshMuxOptions CloneMuxOptions(SshMuxOptions? mux)
    {
        mux ??= new SshMuxOptions();
        return new SshMuxOptions
        {
            Enabled = mux.Enabled,
            ControlMasterAuto = mux.ControlMasterAuto,
            ControlPath = mux.ControlPath,
            ControlPersistSeconds = mux.ControlPersistSeconds
        };
    }

    private static SshProfileStoreSnapshot ToSnapshot(StoreDocument document)
    {
        return new SshProfileStoreSnapshot
        {
            SchemaVersion = document.SchemaVersion,
            Profiles = document.Profiles.Select(CloneProfile).ToList()
        };
    }

    private static StoreDocument FromSnapshot(SshProfileStoreSnapshot snapshot)
    {
        return new StoreDocument
        {
            SchemaVersion = snapshot.SchemaVersion,
            Profiles = snapshot.Profiles.Select(CloneProfile).ToList()
        };
    }

    private sealed class StoreDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<SshProfile> Profiles { get; set; } = new();
    }
}
