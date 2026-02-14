using System;
using System.IO;
using System.Text.Json;

namespace NovaTerminal.Core
{
    public sealed class WorkspacePolicyHooks
    {
        public bool AllowWorkspaceBundleExport { get; set; } = true;
        public bool AllowWorkspaceBundleImport { get; set; } = true;
        public int MaxTabsPerWorkspace { get; set; } = 0; // 0 = unlimited
        public bool RequireSsoForWorkspaceBundles { get; set; } = false;
        public string? SsoAuthorityUrl { get; set; }
        public string? SsoClientId { get; set; }
    }

    public static class WorkspacePolicyManager
    {
        private static readonly object PolicyLock = new();
        private static WorkspacePolicyHooks? _cached;
        private static DateTime _lastReadUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

        public static WorkspacePolicyHooks Current
        {
            get
            {
                lock (PolicyLock)
                {
                    if (_cached != null && (DateTime.UtcNow - _lastReadUtc) < CacheTtl)
                    {
                        return _cached;
                    }

                    _cached = LoadUnsafe();
                    _lastReadUtc = DateTime.UtcNow;
                    return _cached;
                }
            }
        }

        internal static void ResetCacheForTests()
        {
            lock (PolicyLock)
            {
                _cached = null;
                _lastReadUtc = DateTime.MinValue;
            }
        }

        private static WorkspacePolicyHooks LoadUnsafe()
        {
            try
            {
                string path = AppPaths.WorkspacePolicyFilePath;
                if (!File.Exists(path))
                {
                    return new WorkspacePolicyHooks();
                }

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.WorkspacePolicyHooks)
                    ?? new WorkspacePolicyHooks();
            }
            catch
            {
                return new WorkspacePolicyHooks();
            }
        }
    }
}
