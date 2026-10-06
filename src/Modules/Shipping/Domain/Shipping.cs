using System.Text.RegularExpressions;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Shipping.Contracts;

namespace MyOnlineShop.Shipping.Domain;

public sealed class ShippingRuleException(string message) : Exception(message);
public static class ShippingAddress
{
    public static ShippingAddressDto Snapshot(ShippingAddressInput? input)
    {
        if (input is null || new[] { input.Recipient, input.State, input.City, input.Street, input.PostalCode, input.CountryCode }.Any(string.IsNullOrWhiteSpace) ||
            input.Recipient.Length > 200 || input.State.Length > 100 || input.City.Length > 100 || input.Street.Length > 500 || input.PostalCode.Length > 20 ||
            input.CountryCode.Length != 2 || !input.CountryCode.All(char.IsAsciiLetter) || input.PhoneNumber is null ||
            !Regex.IsMatch(input.PhoneNumber, @"^\+[1-9][0-9]{7,14}$") || input.Building?.Length > 100 || input.Unit?.Length > 30)
            throw new ShippingRuleException("Shipping address is invalid.");
        return new(input.Recipient.Trim(), input.PhoneNumber, input.State.Trim(), input.City.Trim(), input.Street.Trim(),
            input.PostalCode.Trim(), input.CountryCode.ToUpperInvariant(), input.Building?.Trim(), input.Unit?.Trim());
    }
    public static ShippingAddressDto Validate(ShippingAddressDto value) => Snapshot(new() { Recipient = value.Recipient, PhoneNumber = value.PhoneNumber,
        State = value.State, City = value.City, Street = value.Street, PostalCode = value.PostalCode, CountryCode = value.CountryCode, Building = value.Building, Unit = value.Unit });
}
public sealed class ShippingMethod
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Code { get; private set; } = "";
    public string? Description { get; private set; }
    public decimal BaseCost { get; private set; }
    public string Currency { get; private set; } = "";
    public bool IsActive { get; private set; }
    public bool RequiresTracking { get; private set; }
    public Guid Revision { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static ShippingMethod Create(ShippingMethodInput input) { var method = new ShippingMethod { Id = Guid.NewGuid() }; method.Update(input); return method; }
    public void Update(ShippingMethodInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > 64 ||
            !Regex.IsMatch(input.Code, @"^[A-Za-z0-9_.-]+$") || input.Description?.Length > 500) throw new ShippingRuleException("Shipping method is invalid.");
        var currency = MoneyRules.Currency(input.Currency); MoneyRules.Amount(input.BaseCost, currency, true);
        Name = input.Name.Trim(); Code = input.Code.ToUpperInvariant(); Description = input.Description?.Trim();
        BaseCost = input.BaseCost; Currency = currency; IsActive = input.IsActive; RequiresTracking = input.RequiresTracking; Revision = Guid.NewGuid();
    }
    public ShippingMethodDto Dto() => new(Id, Name, Code, Description, BaseCost, Currency, IsActive, RequiresTracking, Revision);
}
// Central flat-rate rule. Future pricing rules extend this calculator without changing checkout/consumers.
public static class ShippingCost
{
    public static ShippingQuoteSnapshot Calculate(ShippingMethod method, ShippingAddressInput address, string currency, DateTimeOffset now)
    {
        currency = MoneyRules.Currency(currency);
        if (!method.IsActive || method.Currency != currency) throw new ShippingRuleException("Shipping method is inactive or its currency does not match.");
        var snapshot = ShippingAddress.Snapshot(address); MoneyRules.Amount(method.BaseCost, currency, true);
        return new() { ShippingMethodId = method.Id, MethodName = method.Name, MethodCode = method.Code, Cost = method.BaseCost,
            Currency = currency, RequiresTracking = method.RequiresTracking, Address = snapshot, QuotedAtUtc = now.ToUniversalTime() };
    }
}
public enum ShipmentStatus { Pending, Preparing, Shipped, InTransit, Delivered, Cancelled }
public sealed class Shipment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid ShippingMethodId { get; private set; }
    public string MethodName { get; private set; } = "";
    public string MethodCode { get; private set; } = "";
    public ShippingAddressDto Address { get; private set; } = null!;
    public decimal ShippingCost { get; private set; }
    public string Currency { get; private set; } = "";
    public bool RequiresTracking { get; private set; }
    public ShipmentStatus Status { get; private set; }
    public string? TrackingNumber { get; private set; }
    public string? Carrier { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ShippedAtUtc { get; private set; }
    public DateTimeOffset? DeliveredAtUtc { get; private set; }
    public Guid Revision { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static Shipment Create(Guid order, Guid user, ShippingQuoteSnapshot quote, DateTimeOffset now)
    {
        if (order == Guid.Empty || user == Guid.Empty || quote.ShippingMethodId == Guid.Empty || quote.Address is null ||
            string.IsNullOrWhiteSpace(quote.MethodName) || quote.MethodName.Length > 100 || string.IsNullOrWhiteSpace(quote.MethodCode) || quote.MethodCode.Length > 64)
            throw new ShippingRuleException("Shipment identity or shipping selection is invalid.");
        var currency = MoneyRules.Currency(quote.Currency); MoneyRules.Amount(quote.Cost, currency, true);
        return new() { Id = Guid.NewGuid(), OrderId = order, UserId = user, ShippingMethodId = quote.ShippingMethodId,
            MethodName = quote.MethodName, MethodCode = quote.MethodCode, ShippingCost = quote.Cost, Currency = currency,
            RequiresTracking = quote.RequiresTracking, Address = ShippingAddress.Validate(quote.Address), Status = ShipmentStatus.Pending,
            CreatedAtUtc = now.ToUniversalTime(), Revision = Guid.NewGuid() };
    }
    public void Track(string tracking, string carrier)
    {
        if (Status is not (ShipmentStatus.Pending or ShipmentStatus.Preparing) || string.IsNullOrWhiteSpace(tracking) || tracking.Length > 100 ||
            string.IsNullOrWhiteSpace(carrier) || carrier.Length > 100) throw new ShippingRuleException("Tracking must be valid and assigned before shipment dispatch.");
        TrackingNumber = tracking.Trim(); Carrier = carrier.Trim(); Revision = Guid.NewGuid();
    }
    public void Transition(ShipmentStatus next, DateTimeOffset now)
    {
        var allowed = (Status, next) switch
        {
            (ShipmentStatus.Pending, ShipmentStatus.Preparing or ShipmentStatus.Cancelled) => true,
            (ShipmentStatus.Preparing, ShipmentStatus.Shipped or ShipmentStatus.Cancelled) => true,
            (ShipmentStatus.Shipped, ShipmentStatus.InTransit) => true,
            (ShipmentStatus.InTransit, ShipmentStatus.Delivered) => true,
            _ => false
        };
        if (!allowed) throw new ShippingRuleException("Shipment transition is not allowed.");
        if (next == ShipmentStatus.Shipped && RequiresTracking && (TrackingNumber is null || Carrier is null))
            throw new ShippingRuleException("Carrier and tracking are required before dispatch.");
        if (now < CreatedAtUtc || ShippedAtUtc is { } shipped && now < shipped) throw new ShippingRuleException("Shipment timestamps must be ordered.");
        Status = next; Revision = Guid.NewGuid();
        if (next == ShipmentStatus.Shipped) ShippedAtUtc = now.ToUniversalTime();
        if (next == ShipmentStatus.Delivered) DeliveredAtUtc = now.ToUniversalTime();
    }
    public ShipmentDto Dto() => new(Id, OrderId, ShippingMethodId, MethodName, MethodCode, Address, ShippingCost, Currency, Status.ToString(),
        TrackingNumber, Carrier, CreatedAtUtc, ShippedAtUtc, DeliveredAtUtc, Revision);
}
public sealed class ShippingAudit
{
    public Guid Id { get; private set; }
    public Guid EntityId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; } = "";
    public DateTimeOffset AtUtc { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public static ShippingAudit Record(Guid id, Guid actor, string action, DateTimeOffset now, string correlation) =>
        new() { Id = Guid.NewGuid(), EntityId = id, ActorId = actor, Action = action, AtUtc = now.ToUniversalTime(), CorrelationId = correlation };
}
