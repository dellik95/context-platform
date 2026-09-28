namespace Context.Domain.Enums;

// Stored as an int ordinal: renumbering requires a data migration remapping existing rows.
public enum MemoryType
{
    Fact = 0,
    Decision = 1,
    Constraint = 2,
    Architecture = 3,
    Behavior = 4,
    Configuration = 5,
    Discovery = 6,
    Procedure = 7,
    Issue = 8,
    Event = 9,
}
