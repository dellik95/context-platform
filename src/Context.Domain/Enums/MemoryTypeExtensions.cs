namespace Context.Domain.Enums;

public static class MemoryTypeExtensions
{
    public static bool IsEvent(this MemoryType type) => type == MemoryType.Event;

    public static bool IsKnowledge(this MemoryType type) => type != MemoryType.Event;

    public static MemoryKind Kind(this MemoryType type) => type.IsEvent() ? MemoryKind.Event : MemoryKind.Knowledge;

    /// <summary>Case-insensitive name parse. Blank, unknown or numeric input returns false with
    /// <paramref name="type"/> set to <see cref="MemoryType.Fact"/>.</summary>
    public static bool TryParseName(string? raw, out MemoryType type)
    {
        var trimmed = raw?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && !char.IsAsciiDigit(trimmed[0]) && trimmed[0] != '-'
            && Enum.TryParse(trimmed, ignoreCase: true, out type) && Enum.IsDefined(type))
        {
            return true;
        }

        type = MemoryType.Fact;
        return false;
    }
}
