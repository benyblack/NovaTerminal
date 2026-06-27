using System.ComponentModel;
using ModelContextProtocol.Server;

namespace NovaTerminal.McpServer.Tools;

// Source of truth for field names / enum values:
//   src/NovaTerminal.Platform/Ssh/Models/SshProfile.cs (+ SshJumpHop, PortForward,
//   SshMuxOptions) and SshProfileStoreSnapshot in Ssh/Storage/ISshProfileStore.cs.
// This server has NO ProjectReference to NovaTerminal.Platform by design, so that
// knowledge is hand-mirrored below. A reflection drift-guard test in
// NovaTerminal.McpServer.Tests fails if those types change. Keep both in sync.
[McpServerToolType]
public static class ConnectionProfileTools
{
    [McpServerTool(Name = "novaterminal.get_connection_profile_schema"),
     Description("Returns the schema for a NovaTerminal SSH connection-profile JSON: PascalCase fields grouped by area, integer enum mappings, defaults, and a complete example. The on-disk format uses integer-valued enums; passwords are vault-managed and never stored in profile JSON. Use before authoring or editing a profile.")]
    public static string GetConnectionProfileSchema() =>
        """
        # NovaTerminal connection-profile (SSH) JSON schema

        Profiles are stored at `%LOCALAPPDATA%\NovaTerminal\ssh\profiles.json`. The on-disk
        format uses **PascalCase** field names and **integer-valued enums**. Passwords are
        never stored here — they live in the OS credential vault.

        A single profile is a JSON object. The full file wraps profiles in a document:
        `{ "SchemaVersion": 1, "Profiles": [ <profile>, ... ] }`. The validator accepts
        either shape.

        ## Identity / organization
        | Field | Type | Notes |
        |-------|------|-------|
        | `Id` | string (GUID) | Optional; the store assigns one if absent/invalid. |
        | `Name` | string | **Required**, non-empty. |
        | `GroupPath` | string | Tree path, e.g. "Prod/DB". Default "General". |
        | `Notes` | string | Free text. |
        | `AccentColor` | string | e.g. "#3399EE". |
        | `Tags` | string[] | Labels. |

        ## Connection
        | Field | Type | Notes |
        |-------|------|-------|
        | `Host` | string | **Required**, non-empty. |
        | `User` | string | SSH user. |
        | `Port` | int | 1–65535. Default 22. |
        | `BackendKind` | int enum | 0=OpenSsh, 1=Native. |

        ## Auth
        | Field | Type | Notes |
        |-------|------|-------|
        | `AuthMode` | int enum | 0=Default, 1=Agent, 2=IdentityFile. |
        | `IdentityFilePath` | string | Key path; expected when AuthMode is 2 (IdentityFile). |
        | `RememberPasswordInVault` | bool | Whether to keep the password in the vault. |

        ## Jump hosts
        `JumpHops` is an array of `{ "Host": string, "User": string, "Port": int (1–65535) }`.

        ## Port forwarding
        `Forwards` is an array of objects:
        | Field | Type | Notes |
        |-------|------|-------|
        | `Kind` | int enum | 0=Local, 1=Remote, 2=Dynamic. |
        | `BindAddress` | string | e.g. "127.0.0.1". |
        | `SourcePort` | int | 0–65535. |
        | `DestinationHost` | string | Forward target host. |
        | `DestinationPort` | int | 0–65535. |

        ## Multiplexing
        `MuxOptions` is `{ "Enabled": bool, "ControlMasterAuto": bool, "ControlPath": string, "ControlPersistSeconds": int (>= 0) }`.

        ## Session / shell
        | Field | Type | Notes |
        |-------|------|-------|
        | `ServerAliveIntervalSeconds` | int | >= 1. Default 30. |
        | `ServerAliveCountMax` | int | >= 1. Default 3. |
        | `ExtraSshArgs` | string | Extra ssh CLI args. |
        | `WorkingDirectory` | string | Remote working directory. |
        | `RemoteShellKind` | int enum | 0=Auto, 1=Bash, 2=Zsh, 3=Fish, 4=Pwsh. |

        ## Document wrapper
        | Field | Type | Notes |
        |-------|------|-------|
        | `SchemaVersion` | int | Current = 1. |
        | `Profiles` | object[] | Array of profile objects. |

        ## Example (full document)
        ```json
        {
          "SchemaVersion": 1,
          "Profiles": [
            {
              "Id": "aeb456e4-8dd2-4b33-a74d-cb473de0323b",
              "BackendKind": 0,
              "Name": "prod-db",
              "GroupPath": "Prod/DB",
              "Notes": "primary database jump",
              "AccentColor": "#3399EE",
              "Tags": ["prod", "db"],
              "Host": "prod.internal",
              "User": "svc",
              "Port": 2222,
              "AuthMode": 2,
              "IdentityFilePath": "/home/svc/.ssh/prod_ed25519",
              "RememberPasswordInVault": false,
              "JumpHops": [
                { "Host": "jump-1.internal", "User": "ops", "Port": 22 }
              ],
              "Forwards": [
                { "Kind": 0, "BindAddress": "127.0.0.1", "SourcePort": 5432, "DestinationHost": "db.internal", "DestinationPort": 5432 }
              ],
              "MuxOptions": { "Enabled": false, "ControlMasterAuto": true, "ControlPath": "", "ControlPersistSeconds": 0 },
              "ServerAliveIntervalSeconds": 30,
              "ServerAliveCountMax": 3,
              "ExtraSshArgs": "",
              "WorkingDirectory": "",
              "RemoteShellKind": 0
            }
          ]
        }
        ```
        """;
}
