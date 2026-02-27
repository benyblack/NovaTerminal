using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NovaTerminal.Core;

namespace NovaTerminal.ViewModels.Ssh;

public sealed class SshProfileRowViewModel : INotifyPropertyChanged
{
    private const string FavoriteTag = "favorite";
    private readonly TerminalProfile _profile;
    private string _searchIndex;

    public SshProfileRowViewModel(TerminalProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _profile.Tags ??= new List<string>();
        _searchIndex = BuildSearchIndex();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TerminalProfile Profile => _profile;
    public Guid Id => _profile.Id;
    public string Name => _profile.Name;
    public string Host => _profile.SshHost;
    public string User => _profile.SshUser;
    public int Port => _profile.SshPort > 0 ? _profile.SshPort : 22;
    public string GroupPath => _profile.Group ?? string.Empty;
    public string Notes => _profile.Notes ?? string.Empty;
    public IReadOnlyList<string> Tags => _profile.Tags;
    public string Endpoint => string.IsNullOrWhiteSpace(User)
        ? $"{Host}:{Port}"
        : $"{User}@{Host}:{Port}";

    public bool IsFavorite
    {
        get => _profile.Tags.Any(tag => string.Equals(tag, FavoriteTag, StringComparison.OrdinalIgnoreCase));
        set
        {
            bool current = IsFavorite;
            if (current == value)
            {
                return;
            }

            if (value)
            {
                _profile.Tags.Add(FavoriteTag);
            }
            else
            {
                _profile.Tags.RemoveAll(tag => string.Equals(tag, FavoriteTag, StringComparison.OrdinalIgnoreCase));
            }

            _searchIndex = BuildSearchIndex();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Tags));
        }
    }

    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return _searchIndex.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public void RefreshSearchIndex()
    {
        _searchIndex = BuildSearchIndex();
    }

    private string BuildSearchIndex()
    {
        return string.Join(
            " ",
            Name ?? string.Empty,
            Host ?? string.Empty,
            User ?? string.Empty,
            GroupPath ?? string.Empty,
            Notes ?? string.Empty,
            string.Join(" ", _profile.Tags ?? new List<string>()));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
