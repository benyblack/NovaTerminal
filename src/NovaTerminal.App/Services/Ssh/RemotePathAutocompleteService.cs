using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.Models;

namespace NovaTerminal.Services.Ssh;

public sealed class RemotePathAutocompleteService : IRemotePathAutocompleteService
{
    private const int MaxSuggestions = 12;
    private readonly INativeSshInterop _nativeInterop;
    private readonly ActiveSshSessionRegistry _sessionRegistry;
    private readonly Func<SshConnectionService> _sshServiceFactory;
    private readonly Func<TerminalProfile, string?> _passwordResolver;

    public RemotePathAutocompleteService(
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

    public Task<IReadOnlyList<RemotePathSuggestion>> GetSuggestionsAsync(
        Guid profileId,
        Guid sessionId,
        string input,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(input))
        {
            return Task.FromResult<IReadOnlyList<RemotePathSuggestion>>([]);
        }

        if (!_sessionRegistry.TryGet(sessionId, out ActiveSshSessionDescriptor? descriptor) ||
            descriptor is null ||
            descriptor.ProfileId != profileId ||
            descriptor.BackendKind != SshBackendKind.Native)
        {
            return Task.FromResult<IReadOnlyList<RemotePathSuggestion>>([]);
        }

        try
        {
            SshConnectionService sshService = _sshServiceFactory();
            TerminalProfile? profile = sshService.GetConnectionProfile(profileId);
            if (profile == null || profile.SshBackendKind != SshBackendKind.Native)
            {
                return Task.FromResult<IReadOnlyList<RemotePathSuggestion>>([]);
            }

            RemotePathAutocompleteQuery query = RemotePathAutocompleteQuery.Parse(input);
            NativeSshConnectionOptions baseOptions = SftpService.BuildNativeTransferConnectionOptions(
                sshService,
                profile,
                sshService.GetConnectionProfiles());
            bool prefersIdentityFile = !string.IsNullOrWhiteSpace(baseOptions.IdentityFilePath);
            string? resolvedPassword = prefersIdentityFile
                ? null
                : (_sessionRegistry.TryGetRuntimePassword(sessionId, out string? runtimePassword)
                    ? runtimePassword
                    : _passwordResolver(profile));
            var connectionOptions = new NativeSshConnectionOptions
            {
                Host = baseOptions.Host,
                Port = baseOptions.Port,
                User = baseOptions.User,
                Cols = baseOptions.Cols,
                Rows = baseOptions.Rows,
                Term = baseOptions.Term,
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

            IReadOnlyList<NativeRemotePathEntry> entries = _nativeInterop.ListRemoteDirectory(
                connectionOptions,
                query.ParentPath,
                cancellationToken);

            IReadOnlyList<RemotePathSuggestion> ranked = RemotePathAutocompleteQuery.Rank(
                entries.Select(entry => new RemotePathSuggestion(entry.Name, entry.FullPath, entry.IsDirectory)),
                query.Prefix)
                .Take(MaxSuggestions)
                .ToList();

            return Task.FromResult(ranked);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<RemotePathSuggestion>>([]);
        }
    }
}
