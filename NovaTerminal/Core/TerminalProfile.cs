using System;

namespace NovaTerminal.Core
{
    public enum ConnectionType
    {
        Local,
        SSH
    }

    public enum ForwardingType
    {
        Local,
        Remote,
        Dynamic
    }

    public enum ForwardingStatus
    {
        Starting,
        Active,
        Degraded,
        Failed,
        Stopped
    }

    public class ForwardingRule
    {
        public ForwardingType Type { get; set; } = ForwardingType.Local;
        public string LocalAddress { get; set; } = ""; // e.g. "8080" or "127.0.0.1:8080"
        public string RemoteAddress { get; set; } = ""; // e.g. "10.0.0.5:80" (ignored for Dynamic)

        [System.Text.Json.Serialization.JsonIgnore]
        public ForwardingStatus Status { get; set; } = ForwardingStatus.Stopped;

        [System.Text.Json.Serialization.JsonIgnore]
        public string? LastError { get; set; }

        public override string ToString()
        {
            return Type switch
            {
                ForwardingType.Local => $"-L {LocalAddress}:{RemoteAddress}",
                ForwardingType.Remote => $"-R {RemoteAddress}:{LocalAddress}",
                ForwardingType.Dynamic => $"-D {LocalAddress}",
                _ => ""
            };
        }
    }

    public class TerminalProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "New Profile";
        public string Command { get; set; } = "cmd.exe";
        public string Arguments { get; set; } = "";
        public string StartingDirectory { get; set; } = "";

        // Connection Settings
        public ConnectionType Type { get; set; } = ConnectionType.Local;
        public string SshHost { get; set; } = "";
        public int SshPort { get; set; } = 22;
        public string SshUser { get; set; } = "";
        public string SshKeyPath { get; set; } = "";

        // Advanced SSH
        public Guid? JumpHostProfileId { get; set; }
        public bool UseSshAgent { get; set; } = true;
        public string? IdentityFilePath { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? Password { get; set; }

        // Overrides (null means use global settings)
        public string? ThemeName { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSize { get; set; }
        public bool? EnableLigatures { get; set; }
        public string? CursorStyle { get; set; }
        public bool? CursorBlink { get; set; }

        // SFTP Defaults
        public string? DefaultRemoteDir { get; set; }
        public string? DefaultLocalDir { get; set; }

        public string Group { get; set; } = "General";
        public System.Collections.Generic.List<string> Tags { get; set; } = new();
        public System.Collections.Generic.List<ForwardingRule> Forwards { get; set; } = new();
        public string Icon { get; set; } = "Terminal";
        public DateTime LastUsed { get; set; } = DateTime.MinValue;

        public override string ToString() => Name;

        /// <summary>
        /// Generates the full SSH command including ProxyJump (-J) and Identity (-i) flags.
        /// Recursively resolves jump hosts.
        /// </summary>


        private string GetJumpChain(TerminalProfile current, System.Collections.Generic.List<TerminalProfile> allProfiles, System.Collections.Generic.HashSet<Guid> visited)
        {
            if (visited.Contains(current.Id)) return ""; // Break circle
            visited.Add(current.Id);

            string entry = string.IsNullOrEmpty(current.SshUser) ? current.SshHost : $"{current.SshUser}@{current.SshHost}";
            if (current.SshPort != 22) entry += $":{current.SshPort}";

            if (current.JumpHostProfileId.HasValue)
            {
                var parent = allProfiles.Find(p => p.Id == current.JumpHostProfileId.Value);
                if (parent != null)
                {
                    string parentChain = GetJumpChain(parent, allProfiles, visited);
                    if (!string.IsNullOrEmpty(parentChain))
                    {
                        return $"{parentChain},{entry}"; // Comma separated for -J
                    }
                }
            }

            return entry;
        }

        /// <summary>
        /// Generates ONLY the arguments for ssh.exe (flags + target)
        /// </summary>
        public string GenerateSshArguments(System.Collections.Generic.List<TerminalProfile> allProfiles)
        {
            var sb = new System.Text.StringBuilder();
            var visited = new System.Collections.Generic.HashSet<Guid>();

            // Identity
            if (!UseSshAgent && !string.IsNullOrEmpty(IdentityFilePath))
            {
                sb.Append($" -i \"{IdentityFilePath}\"");
            }
            else if (!string.IsNullOrEmpty(SshKeyPath)) // Backward compat
            {
                sb.Append($" -i \"{SshKeyPath}\"");
            }

            // Jump Host Recursion
            if (JumpHostProfileId.HasValue)
            {
                visited.Add(Id);
                var jumpProfile = allProfiles.Find(p => p.Id == JumpHostProfileId.Value);
                if (jumpProfile != null)
                {
                    string jumpChain = GetJumpChain(jumpProfile, allProfiles, visited);
                    if (!string.IsNullOrEmpty(jumpChain))
                    {
                        sb.Append($" -J {jumpChain}");
                    }
                }
            }

            // Port
            if (SshPort != 22)
            {
                sb.Append($" -p {SshPort}");
            }

            // Port Forwarding
            if (Forwards != null)
            {
                foreach (var rule in Forwards)
                {
                    if (string.IsNullOrWhiteSpace(rule.LocalAddress)) continue;

                    switch (rule.Type)
                    {
                        case ForwardingType.Local:
                            var localRem = string.IsNullOrWhiteSpace(rule.RemoteAddress) ? $"127.0.0.1:{rule.LocalAddress}" : rule.RemoteAddress;
                            sb.Append($" -L {rule.LocalAddress}:{localRem}");
                            break;
                        case ForwardingType.Remote:
                            var remRem = string.IsNullOrWhiteSpace(rule.RemoteAddress) ? $"127.0.0.1:{rule.LocalAddress}" : rule.RemoteAddress;
                            sb.Append($" -R {remRem}:{rule.LocalAddress}");
                            break;
                        case ForwardingType.Dynamic:
                            sb.Append($" -D {rule.LocalAddress}");
                            break;
                    }
                }
            }

            // Target
            if (!string.IsNullOrEmpty(SshUser))
            {
                sb.Append($" {SshUser}@{SshHost}");
            }
            else
            {
                sb.Append($" {SshHost}");
            }

            return sb.ToString().Trim();
        }


        public static TerminalProfile CreateDefault()
        {
            return new TerminalProfile
            {
                Name = "Command Prompt",
                Command = "cmd.exe"
            };
        }
    }
}
