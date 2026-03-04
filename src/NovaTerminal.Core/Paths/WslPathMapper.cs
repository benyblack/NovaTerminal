using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Core.Execution;

namespace NovaTerminal.Core.Paths
{
    public class WslPathMapper : IPathMapper
    {
        private readonly IProcessRunner _processRunner;
        private readonly string? _distroName;
        
        // LRU Cache: (NormalizedWindowsPath) -> WSLPath
        private static readonly ConcurrentDictionary<(string? Distro, string WinPath), string> _cache = new();
        private static readonly Queue<(string? Distro, string WinPath)> _lruKeys = new();
        private const int MaxCacheSize = 512;
        private static readonly object _cacheLock = new();

        public WslPathMapper(IProcessRunner processRunner, string? distroName = null)
        {
            _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
            _distroName = distroName;
        }

        public async Task<string> MapAsync(string hostPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(hostPath)) return hostPath;

            string normalizedWinPath = NormalizeWindowsPath(hostPath);
            var cacheKey = (_distroName, normalizedWinPath);

            if (_cache.TryGetValue(cacheKey, out string? cachedWslPath))
            {
                return cachedWslPath;
            }

            string resultWslPath;
            try
            {
                string arguments = string.IsNullOrWhiteSpace(_distroName)
                    ? $"wslpath -a -u \"{normalizedWinPath}\""
                    : $"-d \"{_distroName}\" wslpath -a -u \"{normalizedWinPath}\"";

                var runResult = await _processRunner.RunProcessAsync("wsl.exe", arguments, ct);
                
                if (runResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(runResult.StdOut))
                {
                    resultWslPath = runResult.StdOut.Trim();
                }
                else
                {
                    // Fallback to original Windows path on failure
                    resultWslPath = hostPath;
                }
            }
            catch
            {
                // Fallback to original Windows path on exception
                resultWslPath = hostPath;
            }

            AddToCache(cacheKey, resultWslPath);
            return resultWslPath;
        }

        private static string NormalizeWindowsPath(string path)
        {
            // Full path, unified casing for drive letter, trim trailing slashes
            path = path.TrimEnd('\\', '/');
            if (path.Length >= 2 && path[1] == ':' && char.IsLower(path[0]))
            {
                path = char.ToUpperInvariant(path[0]) + path.Substring(1);
            }
            return path;
        }

        private static void AddToCache((string? Distro, string WinPath) key, string value)
        {
            lock (_cacheLock)
            {
                if (_cache.ContainsKey(key))
                {
                    return;
                }

                if (_cache.Count >= MaxCacheSize)
                {
                    var oldest = _lruKeys.Dequeue();
                    _cache.TryRemove(oldest, out _);
                }

                _cache[key] = value;
                _lruKeys.Enqueue(key);
            }
        }
    }
}
