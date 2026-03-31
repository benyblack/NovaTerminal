using System.Text.Json;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Storage;

public sealed class JsonSshProfileStore : ISshProfileStore
{
    private const int CurrentSchemaVersion = 1;

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

    private SshStoreDocument LoadDocumentLocked()
    {
        if (!File.Exists(_storeFilePath))
        {
            return CreateNewDocument();
        }

        try
        {
            string json = File.ReadAllText(_storeFilePath);
            var document = JsonSerializer.Deserialize(json, SshJsonContext.Default.SshStoreDocument) ?? CreateNewDocument();
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
        catch (JsonException ex)
        {
            QuarantineCorruptStoreLocked();
            TerminalLogger.Log($"[JsonSshProfileStore] Parsed JSON is corrupt. Store quarantined. Error: {ex.Message}");
            return CreateNewDocument();
        }
        catch (Exception ex)
        {
            TerminalLogger.Log($"[JsonSshProfileStore] Unexpected error reading store: {ex.Message}");
            return CreateNewDocument();
        }
    }

    private void QuarantineCorruptStoreLocked()
    {
        try
        {
            if (File.Exists(_storeFilePath))
            {
                string directory = Path.GetDirectoryName(_storeFilePath) ?? string.Empty;
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                string backupPath = Path.Combine(directory, $"profiles.json.corrupt.{timestamp}.json");
                
                File.Move(_storeFilePath, backupPath);
            }
        }
        catch (Exception ex)
        {
            TerminalLogger.Log($"[JsonSshProfileStore] Failed to quarantine corrupt store: {ex.Message}");
        }
    }

    private SshStoreDocument ApplyMigrationsLocked(SshStoreDocument document)
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

    private void PersistDocumentLocked(SshStoreDocument document)
    {
        document.SchemaVersion = CurrentSchemaVersion;
        SortProfiles(document.Profiles);

        string? directory = Path.GetDirectoryName(_storeFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(document, SshJsonContext.Default.SshStoreDocument);
        
        string tempPath = Path.Combine(directory ?? string.Empty, $"profiles.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, json);
        
        try
        {
            if (File.Exists(_storeFilePath))
            {
                File.Move(tempPath, _storeFilePath, overwrite: true);
            }
            else
            {
                File.Move(tempPath, _storeFilePath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    private static void SortProfiles(List<SshProfile> profiles)
    {
        profiles.Sort((left, right) => left.Id.CompareTo(right.Id));
    }

    private static SshStoreDocument CreateNewDocument()
    {
        return new SshStoreDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Profiles = new List<SshProfile>()
        };
    }

    private static SshProfile NormalizeProfile(SshProfile profile)
    {
        var normalized = CloneProfile(profile);
        normalized.BackendKind = Enum.IsDefined(normalized.BackendKind)
            ? normalized.BackendKind
            : SshBackendKind.OpenSsh;
        normalized.Name = normalized.Name?.Trim() ?? string.Empty;
        normalized.GroupPath = normalized.GroupPath?.Trim() ?? string.Empty;
        normalized.Notes = normalized.Notes?.Trim() ?? string.Empty;
        normalized.AccentColor = normalized.AccentColor?.Trim() ?? string.Empty;
        normalized.Tags = (normalized.Tags ?? new List<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        normalized.Host = normalized.Host?.Trim() ?? string.Empty;
        normalized.User = normalized.User?.Trim() ?? string.Empty;
        normalized.Port = normalized.Port > 0 ? normalized.Port : 22;
        normalized.IdentityFilePath = normalized.IdentityFilePath?.Trim() ?? string.Empty;
        normalized.WorkingDirectory = normalized.WorkingDirectory?.Trim() ?? string.Empty;
        normalized.ServerAliveIntervalSeconds = normalized.ServerAliveIntervalSeconds > 0
            ? normalized.ServerAliveIntervalSeconds
            : 30;
        normalized.ServerAliveCountMax = normalized.ServerAliveCountMax > 0
            ? normalized.ServerAliveCountMax
            : 3;
        normalized.ExtraSshArgs = normalized.ExtraSshArgs?.Trim() ?? string.Empty;
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
            BackendKind = profile.BackendKind,
            Name = profile.Name,
            GroupPath = profile.GroupPath,
            Notes = profile.Notes,
            AccentColor = profile.AccentColor,
            Tags = (profile.Tags ?? new List<string>()).ToList(),
            Host = profile.Host,
            User = profile.User,
            Port = profile.Port,
            AuthMode = profile.AuthMode,
            IdentityFilePath = profile.IdentityFilePath,
            RememberPasswordInVault = profile.RememberPasswordInVault,
            JumpHops = profile.JumpHops.Select(CloneJumpHop).ToList(),
            Forwards = profile.Forwards.Select(CloneForward).ToList(),
            MuxOptions = CloneMuxOptions(profile.MuxOptions),
            ServerAliveIntervalSeconds = profile.ServerAliveIntervalSeconds,
            ServerAliveCountMax = profile.ServerAliveCountMax,
            ExtraSshArgs = profile.ExtraSshArgs,
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

    private static SshProfileStoreSnapshot ToSnapshot(SshStoreDocument document)
    {
        return new SshProfileStoreSnapshot
        {
            SchemaVersion = document.SchemaVersion,
            Profiles = document.Profiles.Select(CloneProfile).ToList()
        };
    }

    private static SshStoreDocument FromSnapshot(SshProfileStoreSnapshot snapshot)
    {
        return new SshStoreDocument
        {
            SchemaVersion = snapshot.SchemaVersion,
            Profiles = snapshot.Profiles.Select(CloneProfile).ToList()
        };
    }
}

internal sealed class SshStoreDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<SshProfile> Profiles { get; set; } = new();
}
