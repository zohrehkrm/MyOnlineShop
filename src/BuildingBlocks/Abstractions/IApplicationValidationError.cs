namespace MyOnlineShop.BuildingBlocks.Abstractions;

public interface IApplicationValidationError : IApplicationError
{
    IReadOnlyDictionary<string, string[]> Errors { get; }
}
