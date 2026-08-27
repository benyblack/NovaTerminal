using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Native;
using NovaTerminal.Platform.Ssh.Storage;
using NovaTerminal.Models;

namespace NovaTerminal.Services.Ssh;

public sealed class RemoteDirectoryBrowserService : IRemoteDirectoryBrowserService
{
    private const string InactiveSessionErrorMessage = "Remote directory listing requires an active native SSH session.";
    private const string MissingProfileErrorMessage = "The SSH connection profile could not be loaded for remote directory listing.";
    private const string UnsupportedProfileErrorMessage = "Remote directory listing requires a native SSH profile.";
    private const string ListingFailedErrorMessage = "Unable to list the remote directory.";

    private readonly INativeSshInterop _nativeInterop;
    private readonly ActiveSshSessionRegistry _sessionRegistry;
    private readonly Func<SshConnectionService> _sshServiceFactory;
    private readonly Func<TerminalProfile, string?> _passwordResolver;

    public RemoteDirectoryBrowserService(
        INativeSshInterop? nativeInterop = null,
        ActiveSshSessionRegistry? sessionRegistry = null,
        Func<SshConnectionService>? sshServiceFactory = null,
        Func<TerminalProfile, string?>? passwordResolver = null)
    {
        _nativeInterop = nativeInterop ?? new NativeSshInterop();
        _sessionRegistry = sessionRegistry ?? ActiveSshSessionRegistry.Instance;
        _sshServiceFactory = sshServiceFactory ?? (() => new SshConnectionService());
        _passwordResolver = passwordResolver ?? (profile => (MainWindow.Vault ?? new VaultService()).GetSshPasswordForProfile(profile));
    }

    public async Task<RemoteSidebarListingResult> ListDirectoryAsync(
        Guid profileId,
        Guid sessionId,
        string remotePath,
        CancellationToken cancellationToken)
    {
        string resolvedPath = NormalizeRemotePath(remotePath);

        if (!TryCreateNativeListingConnection(
                profileId,
                sessionId,
                _sessionRegistry,
                _sshServiceFactory,
                _passwordResolver,
                out NativeSshConnectionOptions? connectionOptions,
                out string errorMessage))
        {
            return RemoteSidebarListingResult.Failure(resolvedPath, errorMessage);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IReadOnlyList<NativeRemotePathEntry> entries = await BackgroundWork.RunBlockingAsync(
                token => _nativeInterop.ListRemoteDirectory(connectionOptions!, resolvedPath, token),
                cancellationToken);

            RemoteSidebarEntry[] mappedEntries = entries
                .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new RemoteSidebarEntry(entry.Name, entry.FullPath, entry.IsDirectory)
                {
                    ModifiedAtUtc = entry.ModifiedAtUtc
                })
                .ToArray();

            return RemoteSidebarListingResult.Success(resolvedPath, mappedEntries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RemoteSidebarListingResult.Failure(resolvedPath, GetErrorMessage(ex, ListingFailedErrorMessage));
        }
    }

    internal static bool TryCreateNativeListingConnection(
        Guid profileId,
        Guid sessionId,
        ActiveSshSessionRegistry sessionRegistry,
        Func<SshConnectionService> sshServiceFactory,
        Func<TerminalProfile, string?> passwordResolver,
        out NativeSshConnectionOptions? connectionOptions,
        out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(sessionRegistry);
        ArgumentNullException.ThrowIfNull(sshServiceFactory);
        ArgumentNullException.ThrowIfNull(passwordResolver);

        connectionOptions = null;
        errorMessage = InactiveSessionErrorMessage;

        if (profileId == Guid.Empty || sessionId == Guid.Empty ||
            !sessionRegistry.TryGetActiveNativeSession(profileId, sessionId, out _))
        {
            return false;
        }

        try
        {
            SshConnectionService sshService = sshServiceFactory();
            TerminalProfile? profile = sshService.GetConnectionProfile(profileId);
            if (profile == null)
            {
                errorMessage = MissingProfileErrorMessage;
                return false;
            }

            if (profile.SshBackendKind != SshBackendKind.Native)
            {
                errorMessage = UnsupportedProfileErrorMessage;
                return false;
            }

            NativeSshConnectionOptions baseOptions = SftpService.BuildNativeTransferConnectionOptions(
                sshService,
                profile,
                sshService.GetConnectionProfiles());
            bool prefersIdentityFile = !string.IsNullOrWhiteSpace(baseOptions.IdentityFilePath);
            string? resolvedPassword = prefersIdentityFile
                ? null
                : (sessionRegistry.TryGetRuntimePassword(sessionId, out string? runtimePassword)
                    ? runtimePassword
                    : passwordResolver(profile));

            connectionOptions = new NativeSshConnectionOptions
            {
                Host = baseOptions.Host,
                Port = baseOptions.Port,
                User = baseOptions.User,
                Cols = baseOptions.Cols,
                Rows = baseOptions.Rows,
                Term = baseOptions.Term,
                KeepAliveIntervalSeconds = baseOptions.KeepAliveIntervalSeconds,
                KeepAliveCountMax = baseOptions.KeepAliveCountMax,
                Password = string.IsNullOrWhiteSpace(resolvedPassword) ? null : resolvedPassword,
                IdentityFilePath = baseOptions.IdentityFilePath,
                UseAgent = baseOptions.UseAgent,
                KnownHostsFilePath = string.IsNullOrWhiteSpace(baseOptions.KnownHostsFilePath)
                    ? AppPaths.NativeKnownHostsFilePath
                    : baseOptions.KnownHostsFilePath,
                JumpHops = baseOptions.JumpHops
                    .Select(hop => new SshJumpHop
                    {
                        Host = hop.Host,
                        User = hop.User,
                        Port = hop.Port
                    })
                    .ToArray()
            };

            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = GetErrorMessage(ex, ListingFailedErrorMessage);
            return false;
        }
    }

    private static string NormalizeRemotePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return "~";
        }

        return TryConvertUncStylePath(remotePath, out string posixPath) ? posixPath : remotePath;
    }

    /// <summary>
    /// A path tracked from OSC 7 on a remote SSH host renders as a Windows UNC path
    /// (<c>\\host\dir</c>) when the shell's reported hostname differs from this machine's own -
    /// see the "local-authority carve-out" remarks on <c>AnsiParser.TryExtractPathFromOsc7</c>.
    /// The SFTP session is already connected to that exact host, so the UNC "host" segment is
    /// redundant; what the native SFTP list call needs is the POSIX path underneath it.
    /// </summary>
    private static bool TryConvertUncStylePath(string path, out string posixPath)
    {
        posixPath = string.Empty;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        string withoutHostPrefix = path[2..];
        int separatorIndex = withoutHostPrefix.IndexOf('\\');
        string remainder = separatorIndex < 0 ? string.Empty : withoutHostPrefix[(separatorIndex + 1)..];
        posixPath = "/" + remainder.Replace('\\', '/');
        return true;
    }

    private static string GetErrorMessage(Exception ex, string fallback)
    {
        return string.IsNullOrWhiteSpace(ex.Message) ? fallback : ex.Message;
    }
}
