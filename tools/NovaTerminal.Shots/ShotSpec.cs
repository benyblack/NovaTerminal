namespace NovaTerminal.Shots;

/// <param name="Intent">
/// What the finished image must show, in plain language. This is the sentence Claude judges
/// the produced PNG against during the /shots review loop, so write it as an observable claim
/// ("the palette is open with results filtered"), not a title.
/// </param>
public sealed record ShotSpec(
    string Name,
    int Tier,
    int LogicalWidth,
    int LogicalHeight,
    string Intent);
