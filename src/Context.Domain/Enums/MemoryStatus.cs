namespace Context.Domain.Enums;

/// <summary>Active and Contested form the live set; Superseded and Obsolete are history.</summary>
public enum MemoryStatus
{
    Active = 0,
    Contested = 1,
    Superseded = 2,
    Obsolete = 3,
}
