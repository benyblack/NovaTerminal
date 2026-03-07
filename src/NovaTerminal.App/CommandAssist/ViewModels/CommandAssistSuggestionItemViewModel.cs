using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed record CommandAssistSuggestionItemViewModel(
    string SelectionGlyph,
    string DisplayText,
    string BadgesText,
    string MetadataText,
    bool IsSelected,
    AssistSuggestionType Type);
