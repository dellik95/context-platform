namespace Context.Domain.Entities;

public class ProviderModel
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public required string ModelName { get; set; }
    public int? Dimensions { get; set; }
}
