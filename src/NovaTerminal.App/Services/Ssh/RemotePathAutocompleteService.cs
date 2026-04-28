using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Models;

namespace NovaTerminal.Services.Ssh;

public sealed class RemotePathAutocompleteService : IRemotePathAutocompleteService
{
    private readonly INativeSshInterop _nativeInterop;
    private readonly ActiveSshSessionRegistry _sessionRegistry;
    private readonly Func<SshConnectionService> _sshServiceFactory;

    public RemotePathAutocompleteService(
        INativeSshInterop? nativeInterop = null,
        ActiveSshSessionRegistry? sessionRegistry = null,
        Func<SshConnectionService>? sshServiceFactory = null)
    {
        _nativeInterop = nativeInterop ?? new NativeSshInterop();
        _sessionRegistry = sessionRegistry ?? ActiveSshSessionRegistry.Instance;
        _sshServiceFactory = sshServiceFactory ?? (() => new SshConnectionService());
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
            NativeSshConnectionOptions connectionOptions = SftpService.BuildNativeTransferConnectionOptions(
                sshService,
                profile,
                sshService.GetConnectionProfiles());

            IReadOnlyList<NativeRemotePathEntry> entries = _nativeInterop.ListRemoteDirectory(
                connectionOptions,
                query.ParentPath,
                cancellationToken);

            IReadOnlyList<RemotePathSuggestion> ranked = RemotePathAutocompleteQuery.Rank(
                entries.Select(entry => new RemotePathSuggestion(entry.Name, entry.FullPath, entry.IsDirectory)),
                query.Prefix);

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
