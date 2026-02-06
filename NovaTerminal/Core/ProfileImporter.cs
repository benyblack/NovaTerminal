using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaTerminal.Core
{
    public static class ProfileImporter
    {
        public static List<TerminalProfile> ImportWindowsTerminalProfiles()
        {
            var profiles = new List<TerminalProfile>();
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                // Common path for Windows Terminal (Stable)
                var wtPath = Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");

                // Fallback for Preview if Stable not found
                if (!File.Exists(wtPath))
                {
                    wtPath = Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json");
                }

                if (!File.Exists(wtPath)) return profiles;

                // Read loosely to avoid comments issue (Standard JSON doesn't support comments, but WT does. 
                // We'll try to strip comments or use strict parsing if they don't use them heavily by default, 
                // but usually File.ReadAllText + specific parser is needed.
                // For simplicity/robustness, let's try standard deserialization with trailing commas allowed 
                // and comment skipping enabled.)

                var jsonOptions = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var jsonString = File.ReadAllText(wtPath);
                using var doc = JsonDocument.Parse(jsonString, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (doc.RootElement.TryGetProperty("profiles", out var profilesElem))
                {
                    JsonElement listElem = profilesElem;
                    // "profiles" can be an object with "list" property (old) or just an array (new/v1.0+)
                    if (profilesElem.ValueKind == JsonValueKind.Object && profilesElem.TryGetProperty("list", out var innerList))
                    {
                        listElem = innerList;
                    }

                    if (listElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in listElem.EnumerateArray())
                        {
                            try
                            {
                                if (item.TryGetProperty("name", out var nameProp) && item.TryGetProperty("commandline", out var cmdProp))
                                {
                                    string name = nameProp.GetString() ?? "Unknown";
                                    string commandLine = cmdProp.GetString() ?? "";

                                    if (string.IsNullOrWhiteSpace(commandLine)) continue;

                                    // Basic command parsing
                                    string command = commandLine;
                                    string args = "";

                                    // very naive split, but sufficient for standard "executable -flag" patterns
                                    // ideally we'd use a proper shell splitter
                                    if (commandLine.Contains(' '))
                                    {
                                        int splitIndex = commandLine.IndexOf(' ');
                                        command = commandLine.Substring(0, splitIndex);
                                        args = commandLine.Substring(splitIndex + 1);
                                    }

                                    var profile = new TerminalProfile
                                    {
                                        Name = name,
                                        Command = command,
                                        Arguments = args,
                                        Type = ConnectionType.Local
                                    };

                                    if (item.TryGetProperty("startingDirectory", out var dirProp))
                                    {
                                        profile.StartingDirectory = dirProp.GetString() ?? "";
                                    }

                                    // Avoid duplicates based on name/command effectively?
                                    // We'll let the caller handle deduplication or just return all found.
                                    profiles.Add(profile);
                                }
                            }
                            catch { /* Skip malformed profile */ }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error importing WT profiles: {ex}");
            }
            return profiles;
        }

        public static List<TerminalProfile> ImportSshConfig()
        {
            var profiles = new List<TerminalProfile>();
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var sshConfigPath = Path.Combine(userProfile, ".ssh", "config");

                if (!File.Exists(sshConfigPath)) return profiles;

                var lines = File.ReadAllLines(sshConfigPath);
                TerminalProfile? currentProfile = null;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

                    var parts = Regex.Split(trimmed, @"\s+");
                    if (parts.Length < 2) continue;

                    string key = parts[0].ToLowerInvariant();
                    string value = parts[1];

                    if (key == "host")
                    {
                        // Save previous if exists
                        if (currentProfile != null && !string.IsNullOrWhiteSpace(currentProfile.SshHost))
                        {
                            // Default name is the host if not set
                            profiles.Add(currentProfile);
                        }

                        // Start new profile
                        // "Host *" is often used for wildcards, skip it for now unless we want a template
                        if (value.Contains('*') || value.Contains('?'))
                        {
                            currentProfile = null;
                            continue;
                        }

                        currentProfile = new TerminalProfile
                        {
                            Name = $"SSH {value}",
                            Type = ConnectionType.SSH,
                            SshHost = value, // Default, might be overridden by HostName
                            Command = "ssh", // Placeholder
                            Arguments = ""
                        };
                    }
                    else if (currentProfile != null)
                    {
                        if (key == "hostname")
                        {
                            currentProfile.SshHost = value;
                        }
                        else if (key == "user")
                        {
                            currentProfile.SshUser = value;
                        }
                        else if (key == "identityfile")
                        {
                            currentProfile.SshKeyPath = value;
                        }
                        else if (key == "port")
                        {
                            if (int.TryParse(value, out int port))
                            {
                                currentProfile.SshPort = port;
                            }
                        }
                    }
                }

                if (currentProfile != null && !string.IsNullOrWhiteSpace(currentProfile.SshHost))
                {
                    profiles.Add(currentProfile);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error importing SSH config: {ex}");
            }
            return profiles;
        }
    }
}
