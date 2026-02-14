using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        public static bool ExportWorkspaceBundle(string workspaceName, string outputPath, string? exportedBy = null)
        {
            string safeName = SanitizeName(workspaceName);
            if (string.IsNullOrWhiteSpace(safeName) || string.IsNullOrWhiteSpace(outputPath))
            {
                AppendWorkspaceAudit("workspace-export", safeName, success: false, "invalid-input");
                return false;
            }

            var session = LoadWorkspace(safeName);
            if (session == null)
            {
                AppendWorkspaceAudit("workspace-export", safeName, success: false, "workspace-not-found");
                return false;
            }

            return ExportWorkspaceBundle(safeName, session, outputPath, exportedBy);
        }

        public static bool ExportWorkspaceBundle(string workspaceName, NovaSession session, string outputPath, string? exportedBy = null)
        {
            string safeName = SanitizeName(workspaceName);
            if (session == null || string.IsNullOrWhiteSpace(safeName) || string.IsNullOrWhiteSpace(outputPath))
            {
                AppendWorkspaceAudit("workspace-export", safeName, success: false, "invalid-input");
                return false;
            }

            try
            {
                string payloadJson = JsonSerializer.Serialize(session, SessionSerializationContext.Default.NovaSession);
                string payloadHash = ComputeSha256Hex(payloadJson);

                var package = new WorkspaceBundlePackage
                {
                    Version = 1,
                    WorkspaceName = safeName,
                    CreatedUtc = DateTime.UtcNow,
                    ExportedBy = exportedBy,
                    PayloadJson = payloadJson,
                    PayloadHashSha256 = payloadHash
                };

                string? outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }

                string packageJson = JsonSerializer.Serialize(package, SessionSerializationContext.Default.WorkspaceBundlePackage);
                File.WriteAllText(outputPath, packageJson);
                AppendWorkspaceAudit("workspace-export", safeName, success: true, $"path={outputPath};hash={payloadHash}");
                return true;
            }
            catch (Exception ex)
            {
                AppendWorkspaceAudit("workspace-export", safeName, success: false, ex.Message);
                return false;
            }
        }

        public static bool VerifyWorkspaceBundle(string bundlePath, out string? workspaceName, out string? error)
        {
            workspaceName = null;
            if (!TryReadBundle(bundlePath, out var bundle, out error))
            {
                return false;
            }

            workspaceName = bundle!.WorkspaceName;
            return true;
        }

        public static bool ImportWorkspaceBundle(string bundlePath, string? workspaceName, out string? error)
        {
            error = null;
            if (!TryReadBundle(bundlePath, out var bundle, out error))
            {
                AppendWorkspaceAudit("workspace-import", workspaceName ?? "unknown", success: false, error ?? "bundle-invalid");
                return false;
            }

            NovaSession? session;
            try
            {
                session = JsonSerializer.Deserialize(bundle!.PayloadJson, SessionSerializationContext.Default.NovaSession);
            }
            catch (Exception ex)
            {
                error = $"bundle-payload-invalid: {ex.Message}";
                AppendWorkspaceAudit("workspace-import", workspaceName ?? bundle!.WorkspaceName, success: false, error);
                return false;
            }

            if (session == null)
            {
                error = "bundle-payload-empty";
                AppendWorkspaceAudit("workspace-import", workspaceName ?? bundle!.WorkspaceName, success: false, error);
                return false;
            }

            string targetName = SanitizeName(string.IsNullOrWhiteSpace(workspaceName) ? bundle!.WorkspaceName : workspaceName!);
            if (string.IsNullOrWhiteSpace(targetName))
            {
                error = "workspace-name-invalid";
                AppendWorkspaceAudit("workspace-import", targetName, success: false, error);
                return false;
            }

            bool saved = SaveWorkspace(targetName, session);
            if (!saved)
            {
                error = "workspace-save-failed";
                AppendWorkspaceAudit("workspace-import", targetName, success: false, error);
                return false;
            }

            AppendWorkspaceAudit("workspace-import", targetName, success: true, $"source={bundlePath};hash={bundle!.PayloadHashSha256}");
            return true;
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

        private static bool TryReadBundle(string bundlePath, out WorkspaceBundlePackage? bundle, out string? error)
        {
            bundle = null;
            error = null;

            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                error = "bundle-file-not-found";
                return false;
            }

            try
            {
                string json = File.ReadAllText(bundlePath);
                bundle = JsonSerializer.Deserialize(json, SessionSerializationContext.Default.WorkspaceBundlePackage);
            }
            catch (Exception ex)
            {
                error = $"bundle-read-failed: {ex.Message}";
                return false;
            }

            if (bundle == null)
            {
                error = "bundle-empty";
                return false;
            }

            if (bundle.Version != 1)
            {
                error = $"bundle-version-unsupported:{bundle.Version}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(bundle.PayloadJson) || string.IsNullOrWhiteSpace(bundle.PayloadHashSha256))
            {
                error = "bundle-missing-payload";
                return false;
            }

            string actualHash = ComputeSha256Hex(bundle.PayloadJson);
            if (!string.Equals(actualHash, bundle.PayloadHashSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "bundle-hash-mismatch";
                return false;
            }

            return true;
        }

        private static string ComputeSha256Hex(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        private static void AppendWorkspaceAudit(string action, string? workspaceName, bool success, string details)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.WorkspaceAuditLogPath)!);
                string ts = DateTime.UtcNow.ToString("O");
                string safeWorkspace = string.IsNullOrWhiteSpace(workspaceName) ? "unknown" : workspaceName.Trim();
                string safeDetails = (details ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
                string line = $"{ts}|{action}|{(success ? "ok" : "fail")}|{safeWorkspace}|{safeDetails}";
                File.AppendAllText(AppPaths.WorkspaceAuditLogPath, line + Environment.NewLine);
            }
            catch
            {
                // Best-effort only.
            }
        }
    }
}
