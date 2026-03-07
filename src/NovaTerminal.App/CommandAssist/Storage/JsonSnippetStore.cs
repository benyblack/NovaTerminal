using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.Core;

namespace NovaTerminal.CommandAssist.Storage;

public sealed class JsonSnippetStore : ISnippetStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSnippetStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<IReadOnlyList<CommandSnippet>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadSnippetsUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(CommandSnippet snippet, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<CommandSnippet> snippets = await LoadSnippetsUnsafeAsync(cancellationToken);
            int existingIndex = snippets.FindIndex(x => x.Id == snippet.Id);
            if (existingIndex >= 0)
            {
                snippets[existingIndex] = snippet;
            }
            else
            {
                snippets.Add(snippet);
            }

            snippets = snippets
                .OrderByDescending(x => x.IsPinned)
                .ThenBy(x => x.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            await SaveSnippetsUnsafeAsync(snippets, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string snippetId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<CommandSnippet> snippets = await LoadSnippetsUnsafeAsync(cancellationToken);
            snippets.RemoveAll(x => x.Id == snippetId);
            await SaveSnippetsUnsafeAsync(snippets, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<CommandSnippet>> LoadSnippetsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<CommandSnippet>();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            List<CommandSnippet>? snippets = await JsonSerializer.DeserializeAsync(
                stream,
                AppJsonContext.Default.ListCommandSnippet,
                cancellationToken);

            return snippets ?? new List<CommandSnippet>();
        }
        catch
        {
            return new List<CommandSnippet>();
        }
    }

    private async Task SaveSnippetsUnsafeAsync(List<CommandSnippet> snippets, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, snippets, AppJsonContext.Default.ListCommandSnippet, cancellationToken);
    }
}
