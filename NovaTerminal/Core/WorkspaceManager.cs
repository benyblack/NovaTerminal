using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NovaTerminal.Core
{
    public static class WorkspaceManager
    {
        private static string WorkspacesDir => AppPaths.WorkspacesDirectory;

        public static IReadOnlyList<string> ListWorkspaceNames()
        {
            try
            {
                if (!Directory.Exists(WorkspacesDir)) return Array.Empty<string>();
                return Directory.GetFiles(WorkspacesDir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public static bool SaveWorkspace(string name, NovaSession session)
        {
            try
            {
                string safeName = SanitizeName(name);
                if (string.IsNullOrWhiteSpace(safeName)) return false;

                Directory.CreateDirectory(WorkspacesDir);
                string path = Path.Combine(WorkspacesDir, safeName + ".json");
                string json = JsonSerializer.Serialize(session, SessionSerializationContext.Default.NovaSession);
                File.WriteAllText(path, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static NovaSession? LoadWorkspace(string name)
        {
            try
            {
                string safeName = SanitizeName(name);
                if (string.IsNullOrWhiteSpace(safeName)) return null;

                string path = Path.Combine(WorkspacesDir, safeName + ".json");
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, SessionSerializationContext.Default.NovaSession);
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return cleaned;
        }
    }
}
