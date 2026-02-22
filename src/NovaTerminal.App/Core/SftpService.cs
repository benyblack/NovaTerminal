using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;

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

    public class TransferJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid SessionId { get; set; }
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
                var profile = settings.Profiles.Find(p => p.Name == job.ProfileName);
                if (profile == null) throw new Exception("Profile not found");

                string scpExe = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "scp.exe" : "scp";
                var args = new System.Text.StringBuilder();

                // Recursion
                if (job.Kind == TransferKind.Folder) args.Append(" -r");

                // Identity / Port / JumpHost (Reuse profile logic but adapt for SCP)
                if (!profile.UseSshAgent && !string.IsNullOrEmpty(profile.IdentityFilePath))
                    args.Append($" -i \"{profile.IdentityFilePath}\"");
                else if (!string.IsNullOrEmpty(profile.SshKeyPath))
                    args.Append($" -i \"{profile.SshKeyPath}\"");

                if (profile.SshPort != 22) args.Append($" -P {profile.SshPort}"); // scp uses -P

                if (profile.JumpHostProfileId.HasValue)
                {
                    var visited = new HashSet<Guid>();
                    var sshArgs = profile.GenerateSshArguments(settings.Profiles);
                    // Extract -J if present. This is a bit hacky but avoids duplicating recursive jump logic
                    if (sshArgs.Contains("-J "))
                    {
                        var parts = sshArgs.Split("-J ");
                        if (parts.Length > 1)
                        {
                            var jumpChain = parts[1].Trim().Split(" ")[0];
                            args.Append($" -J {jumpChain}");
                        }
                    }
                }

                // Batch mode to avoid hang on auth prompts
                args.Append(" -B");

                // Source and Destination
                string remotePart = string.IsNullOrEmpty(profile.SshUser) ? profile.SshHost : $"{profile.SshUser}@{profile.SshHost}";

                if (job.Direction == TransferDirection.Upload)
                {
                    args.Append($" \"{job.LocalPath.Replace("\\", "/")}\" {remotePart}:\"{job.RemotePath}\"");
                }
                else
                {
                    args.Append($" {remotePart}:\"{job.RemotePath}\" \"{job.LocalPath.Replace("\\", "/")}\"");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = scpExe,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) throw new Exception("Failed to start scp process");

                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    throw new Exception(error);
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
    }
}
