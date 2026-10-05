namespace MyOnlineShop.BuildingBlocks.Abstractions;

// Only safe client-facing text may be supplied by an application error.
public interface IApplicationError
{
    int StatusCode { get; }
    string Code { get; }
    string SafeMessage { get; }
}
