namespace NovaTerminal.CommandAssist.ViewModels;

/// <summary>
/// How much of the shortcut hint strip the bubble has room to render.
/// </summary>
/// <remarks>
/// <para>
/// The three rungs of the UX-polish round's progressive collapse. The rule they encode is that the
/// hint strip is the lowest-priority thing in the bubble and must give up its width before the
/// suggestion gives up a single character: the owner's bubble read <c>Suggest | dock | doc...</c>
/// because the hint sat in an <c>Auto</c> column beside the suggestion's <c>*</c> and simply took
/// what it wanted.
/// </para>
/// <para>
/// Two rungs were not enough. The single boolean this replaces went straight from a ~200 px strip to
/// nothing at the 320 px compact threshold, and the widths in between - which is where a normally
/// split pane lives - got the full strip and an unreadable suggestion.
/// </para>
/// </remarks>
public enum AssistHintDetail
{
    /// <summary>Keys and verbs: "Down browse | Ctrl+Enter insert | Esc close".</summary>
    Full,

    /// <summary>Keys only: "Down | Ctrl+Enter | Esc". Reminds rather than teaches.</summary>
    Terse,

    /// <summary>Nothing. The popup footer is the only place the shortcuts appear at this width.</summary>
    Hidden
}
