using NovaTerminal.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Native;
using NovaTerminal.Platform.Ssh.Storage;
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
        _passwordResolver = passwordResolver ?? (profile => (MainWindow.Vault ?? new VaultService()).GetSshPasswordForProfile(profile));
    }

    public async Task<IReadOnlyList<RemotePathSuggestion>> GetSuggestionsAsync(
        Guid profileId,
        Guid sessionId,
        string input,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!RemoteDirectoryBrowserService.TryCreateNativeListingConnection(
                    profileId,
                    sessionId,
                    _sessionRegistry,
                    _sshServiceFactory,
                    _passwordResolver,
                    out NativeSshConnectionOptions? connectionOptions,
                    out _))
            {
                return [];
            }

            RemotePathAutocompleteQuery query = RemotePathAutocompleteQuery.Parse(input);

            IReadOnlyList<NativeRemotePathEntry> entries = await BackgroundWork.RunBlockingAsync(
                token => _nativeInterop.ListRemoteDirectory(connectionOptions!, query.ParentPath, token),
                cancellationToken);

            return RemotePathAutocompleteQuery.Rank(
                    entries.Select(entry => new RemotePathSuggestion(entry.Name, entry.FullPath, entry.IsDirectory)),
                    query.Prefix)
                .Take(MaxSuggestions)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }
}
