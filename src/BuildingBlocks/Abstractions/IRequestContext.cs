namespace MyOnlineShop.BuildingBlocks.Abstractions;

public interface IRequestContext
{
    string CorrelationId { get; }
}
