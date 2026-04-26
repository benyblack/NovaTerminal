using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Core
{
    public enum TransferDirection
    {
        Upload,
        Download
    }

    public enum TransferKind
    {
        File,
        Folder
    }

    public enum TransferState
    {
        Queued,
        Running,
        Completed,
        Failed,
        Canceled
    }

    internal enum SftpTransferBackend
    {
        ExternalScp,
        NativeSftp
    }

    public class TransferJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid SessionId { get; set; }
        public Guid ProfileId { get; set; }
        public string ProfileName { get; set; } = "";
        public TransferDirection Direction { get; set; }
        public TransferKind Kind { get; set; }
        public string LocalPath { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public TransferState State { get; set; } = TransferState.Queued;
        public double Progress { get; set; } // 0..1
        public long BytesTotal { get; set; }
        public long BytesDone { get; set; }
        public string? LastError { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        public string FileName => Path.GetFileName(LocalPath);
    }

    public class SftpService
    {
        private static SftpService? _instance;
        public static SftpService Instance => _instance ??= new SftpService();

        public ObservableCollection<TransferJob> Jobs { get; } = new();

        private SftpService() { }

        public void AddJob(TransferJob job)
        {
            Dispatcher.UIThread.Post(() => Jobs.Add(job));
            // In a real implementation, we would start a queue worker here.
            // For v1, we'll start it immediately.
            Task.Run(() => RunJobAsync(job));
        }

        public event EventHandler<TransferJob>? JobUpdated;

        private async Task RunJobAsync(TransferJob job)
        {
            Dispatcher.UIThread.Post(() =>
            {
                job.State = TransferState.Running;
                job.StartedAt = DateTime.Now;
                JobUpdated?.Invoke(this, job);
            });

            try
            {
                var settings = TerminalSettings.Load();
                settings.Profiles ??= new List<TerminalProfile>();
                var sshService = new SshConnectionService();
                IReadOnlyList<TerminalProfile> storeProfiles = sshService.GetConnectionProfiles();
                TerminalProfile? profile = ResolveProfileForJob(job, settings.Profiles, storeProfiles);

                if (profile == null) throw new Exception("Profile not found");

                switch (SelectTransferBackend(profile))
                {
                    case SftpTransferBackend.NativeSftp:
                        await RunNativeSftpJobAsync(job, profile);
                        break;
                    case SftpTransferBackend.ExternalScp:
                    default:
                        await RunExternalScpJobAsync(job, profile, settings.Profiles, sshService);
                        break;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    job.State = TransferState.Completed;
                    job.Progress = 1.0;
                    job.FinishedAt = DateTime.Now;
                    JobUpdated?.Invoke(this, job);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    job.State = TransferState.Failed;
                    job.LastError = ex.Message;
                    job.FinishedAt = DateTime.Now;
                    JobUpdated?.Invoke(this, job);
                });
            }
        }

        internal static SftpTransferBackend SelectTransferBackend(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            return profile.SshBackendKind == NovaTerminal.Core.Ssh.Models.SshBackendKind.Native
                ? SftpTransferBackend.NativeSftp
                : SftpTransferBackend.ExternalScp;
        }

        internal static NativeSshConnectionOptions BuildNativeTransferConnectionOptions(
            SshConnectionService sshService,
            TerminalProfile profile,
            IReadOnlyList<TerminalProfile>? allProfiles = null)
        {
            ArgumentNullException.ThrowIfNull(sshService);
            ArgumentNullException.ThrowIfNull(profile);

            var nativeConnector = new NativeJumpHostConnector();
            var resolvedProfile = sshService.GetStoredProfile(profile.Id)
                ?? SshConnectionService.MapLegacyTerminalProfile(profile, EnsureLegacyJumpHostProfiles(profile, allProfiles));
            return nativeConnector.CreateConnectionOptions(resolvedProfile, cols: 120, rows: 30);
        }

        private static IReadOnlyList<TerminalProfile>? EnsureLegacyJumpHostProfiles(
            TerminalProfile profile,
            IReadOnlyList<TerminalProfile>? allProfiles)
        {
            if (!profile.JumpHostProfileId.HasValue)
            {
                return allProfiles;
            }

            if (allProfiles == null || allProfiles.Count == 0)
            {
                throw new InvalidOperationException("allProfiles is required when building native transfer options from a legacy profile with jump hosts.");
            }

            return allProfiles;
        }

        internal static TerminalProfile? ResolveProfileForJob(
            TransferJob job,
            IReadOnlyList<TerminalProfile>? localProfiles,
            IReadOnlyList<TerminalProfile>? storeProfiles)
        {
            ArgumentNullException.ThrowIfNull(job);

            IEnumerable<TerminalProfile> localSsh = (localProfiles ?? Array.Empty<TerminalProfile>())
                .Where(profile => profile.Type == ConnectionType.SSH);
            IEnumerable<TerminalProfile> storeSsh = (storeProfiles ?? Array.Empty<TerminalProfile>())
                .Where(profile => profile.Type == ConnectionType.SSH);

            if (job.ProfileId != Guid.Empty)
            {
                TerminalProfile? byId = storeSsh.FirstOrDefault(profile => profile.Id == job.ProfileId)
                    ?? localSsh.FirstOrDefault(profile => profile.Id == job.ProfileId);
                if (byId != null)
                {
                    return byId;
                }
            }

            string name = job.ProfileName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            List<TerminalProfile> storeMatches = storeSsh
                .Where(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (storeMatches.Count == 1)
            {
                return storeMatches[0];
            }
            if (storeMatches.Count > 1)
            {
                return null;
            }

            List<TerminalProfile> localMatches = localSsh
                .Where(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (localMatches.Count == 1)
            {
                return localMatches[0];
            }

            return null;
        }

        private static async Task RunExternalScpJobAsync(
            TransferJob job,
            TerminalProfile profile,
            IReadOnlyList<TerminalProfile>? allProfiles,
            SshConnectionService sshService)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(sshService);

            SshLaunchDetails? launchDetails = null;
            try
            {
                launchDetails = sshService.BuildLaunchDetails(profile, SshDiagnosticsLevel.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SftpService] SSH launch details unavailable for '{profile.Name}', using legacy SCP args: {ex.Message}");
            }

            string scpExe = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "scp.exe" : "scp";
            string args = BuildScpArguments(job, profile, launchDetails, allProfiles);

            ProcessStartInfo startInfo = CreateScpStartInfo(scpExe, args, profile);

            using var process = Process.Start(startInfo);
            if (process == null) throw new Exception("Failed to start scp process");

            string error = startInfo.RedirectStandardError
                ? await process.StandardError.ReadToEndAsync()
                : string.Empty;
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new Exception(string.IsNullOrWhiteSpace(error)
                    ? $"scp exited with code {process.ExitCode}."
                    : error);
            }
        }

        private static Task RunNativeSftpJobAsync(TransferJob job, TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentNullException.ThrowIfNull(profile);

            throw new NotImplementedException("Native SFTP transfers are not implemented yet.");
        }

        internal static string BuildScpArguments(
            TransferJob job,
            TerminalProfile profile,
            SshLaunchDetails? launchDetails,
            IReadOnlyList<TerminalProfile>? allProfiles = null)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentNullException.ThrowIfNull(profile);

            var args = new System.Text.StringBuilder();

            if (job.Kind == TransferKind.Folder)
            {
                args.Append(" -r");
            }

            string remotePart;
            if (launchDetails != null &&
                !string.IsNullOrWhiteSpace(launchDetails.ConfigPath) &&
                !string.IsNullOrWhiteSpace(launchDetails.Alias))
            {
                args.Append(" -F ").Append(QuoteArg(launchDetails.ConfigPath));
                remotePart = launchDetails.Alias;
            }
            else
            {
                if (!profile.UseSshAgent && !string.IsNullOrEmpty(profile.IdentityFilePath))
                {
                    args.Append(" -i ").Append(QuoteArg(profile.IdentityFilePath));
                }
                else if (!string.IsNullOrEmpty(profile.SshKeyPath))
                {
                    args.Append(" -i ").Append(QuoteArg(profile.SshKeyPath));
                }

                if (profile.SshPort != 22)
                {
                    args.Append($" -P {profile.SshPort}");
                }

                if (profile.JumpHostProfileId.HasValue)
                {
                    string sshArgs = profile.GenerateSshArguments((allProfiles ?? new List<TerminalProfile> { profile }).ToList());
                    if (sshArgs.Contains("-J ", StringComparison.Ordinal))
                    {
                        string[] parts = sshArgs.Split("-J ", StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            string jumpChain = parts[1].Trim().Split(" ")[0];
                            args.Append($" -J {jumpChain}");
                        }
                    }
                }

                remotePart = string.IsNullOrEmpty(profile.SshUser) ? profile.SshHost : $"{profile.SshUser}@{profile.SshHost}";
            }

            args.Append(" -O");
            if (ShouldUseBatchMode(profile))
            {
                args.Append(" -B");
            }

            string localPath = QuoteArg(job.LocalPath.Replace("\\", "/", StringComparison.Ordinal));
            string remotePath = QuoteArg(job.RemotePath);
            if (job.Direction == TransferDirection.Upload)
            {
                args.Append(' ').Append(localPath).Append(' ').Append(remotePart).Append(':').Append(remotePath);
            }
            else
            {
                args.Append(' ').Append(remotePart).Append(':').Append(remotePath).Append(' ').Append(localPath);
            }

            return args.ToString();
        }

        internal static ProcessStartInfo CreateScpStartInfo(string scpExe, string args, TerminalProfile profile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scpExe);
            ArgumentNullException.ThrowIfNull(profile);

            bool hideProcess = !RequiresVisibleAuthenticationPrompt(profile);
            var startInfo = new ProcessStartInfo
            {
                FileName = scpExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = hideProcess,
                CreateNoWindow = hideProcess
            };

            if (RequiresAskPass(profile))
            {
                ConfigureAskPass(startInfo, profile);
            }

            return startInfo;
        }

        private static bool ShouldUseBatchMode(TerminalProfile profile)
        {
            return !RequiresAskPass(profile);
        }

        private static bool RequiresAskPass(TerminalProfile profile)
        {
            return profile.SshBackendKind == NovaTerminal.Core.Ssh.Models.SshBackendKind.Native;
        }

        private static bool RequiresVisibleAuthenticationPrompt(TerminalProfile profile)
        {
            return RequiresAskPass(profile) && string.IsNullOrWhiteSpace(ResolveAskPassExecutablePath());
        }

        private static void ConfigureAskPass(ProcessStartInfo startInfo, TerminalProfile profile)
        {
            string? askPassPath = ResolveAskPassExecutablePath();
            if (string.IsNullOrWhiteSpace(askPassPath))
            {
                return;
            }

            startInfo.Environment[SshAskPassCommand.ModeEnvironmentVariable] = "1";
            startInfo.Environment["SSH_ASKPASS"] = askPassPath;
            startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            startInfo.Environment["DISPLAY"] = "NovaTerminal";
            startInfo.Environment[SshAskPassCommand.ProfileIdEnvironmentVariable] = profile.Id.ToString();
            startInfo.Environment[SshAskPassCommand.ProfileNameEnvironmentVariable] = profile.Name ?? string.Empty;
            startInfo.Environment[SshAskPassCommand.ProfileUserEnvironmentVariable] = profile.SshUser ?? string.Empty;
            startInfo.Environment[SshAskPassCommand.ProfileHostEnvironmentVariable] = profile.SshHost ?? string.Empty;
            startInfo.Environment[SshAskPassCommand.ProfilePortEnvironmentVariable] = profile.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string? ResolveAskPassExecutablePath()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string fileName = OperatingSystem.IsWindows()
                ? "NovaTerminal.Cli.exe"
                : "NovaTerminal.Cli";
            string cliPath = Path.Combine(baseDirectory, fileName);
            if (File.Exists(cliPath))
            {
                return cliPath;
            }

            return null;
        }

        private static string QuoteArg(string value)
        {
            string normalized = value ?? string.Empty;
            return $"\"{normalized.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }
    }
}
