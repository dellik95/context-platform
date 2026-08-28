using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class Provider
{
    public Guid Id { get; set; }
    public ProviderCategory Category { get; set; }
    public required string Name { get; set; }           // "openai" | "ollama" | "local-onnx" | "anthropic"
    public string? ConfigJson { get; set; }              // never raw secrets — see Context.Claude §7
    public bool IsDefault { get; set; }
}
