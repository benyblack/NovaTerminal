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
        _passwordResolver = passwordResolver ?? (profile => new VaultService().GetSshPasswordForProfile(profile));
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
                KnownHostsFilePath = string.IsNullOrWhiteSpace(baseOptions.KnownHostsFilePath)
                    ? AppPaths.NativeKnownHostsFilePath
                    : baseOptions.KnownHostsFilePath,
                JumpHost = baseOptions.JumpHost == null
                    ? null
                    : new SshJumpHop
                    {
                        Host = baseOptions.JumpHost.Host,
                        User = baseOptions.JumpHost.User,
                        Port = baseOptions.JumpHost.Port
                    }
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
        return string.IsNullOrWhiteSpace(remotePath) ? "~" : remotePath;
    }

    private static string GetErrorMessage(Exception ex, string fallback)
    {
        return string.IsNullOrWhiteSpace(ex.Message) ? fallback : ex.Message;
    }
}
