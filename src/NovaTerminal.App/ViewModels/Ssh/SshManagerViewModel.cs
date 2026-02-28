using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Launch;

namespace NovaTerminal.ViewModels.Ssh;

public enum SshQuickOpenTarget
{
    CurrentPane = 0,
    NewTab = 1,
    SplitHorizontal = 2,
    SplitVertical = 3
}

public sealed class SshManagerViewModel : INotifyPropertyChanged
{
    private readonly List<SshProfileRowViewModel> _allRows = new();
    private string _searchText = string.Empty;
    private SshDiagnosticsLevel _diagnosticsLevel = SshDiagnosticsLevel.None;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<SshProfileRowViewModel, SshQuickOpenTarget, SshDiagnosticsLevel>? OpenRequested;

    public ObservableCollection<SshProfileRowViewModel> FilteredRows { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public SshDiagnosticsLevel DiagnosticsLevel
    {
        get => _diagnosticsLevel;
        set => SetField(ref _diagnosticsLevel, value);
    }

    public void LoadProfiles(IEnumerable<TerminalProfile> profiles)
    {
        _allRows.Clear();

        foreach (TerminalProfile profile in profiles.Where(profile => profile.Type == ConnectionType.SSH))
        {
            _allRows.Add(new SshProfileRowViewModel(profile));
        }

        ApplyFilter();
    }

    public void ToggleFavorite(SshProfileRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.IsFavorite = !row.IsFavorite;
        ApplyFilter();
    }

    public void RequestOpen(SshProfileRowViewModel row, SshQuickOpenTarget target)
    {
        ArgumentNullException.ThrowIfNull(row);
        OpenRequested?.Invoke(row, target, DiagnosticsLevel);
    }

    public IReadOnlyList<TerminalProfile> GetAllProfiles()
    {
        return _allRows.Select(row => row.Profile).ToArray();
    }

    private void ApplyFilter()
    {
        string query = SearchText?.Trim() ?? string.Empty;
        var filtered = _allRows
            .Where(row => row.Matches(query))
            .OrderByDescending(row => row.IsFavorite)
            .ThenBy(row => row.GroupPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id)
            .ToList();

        FilteredRows.Clear();
        foreach (SshProfileRowViewModel row in filtered)
        {
            FilteredRows.Add(row);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
