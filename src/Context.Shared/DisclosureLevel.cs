namespace Context.Shared;

/// <summary>
/// Progressive-disclosure levels used when expanding a graph node or search hit:
/// metadata-only, a summarized view, or the full content.
/// </summary>
public enum DisclosureLevel
{
    L0_Metadata,
    L1_Summary,
    L2_Full
}
