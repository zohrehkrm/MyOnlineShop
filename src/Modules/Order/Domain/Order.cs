using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Shipping.Contracts;

namespace MyOnlineShop.Order.Domain;

public enum OrderStatus { Pending, AwaitingPayment, Paid, Processing, Shipped, Completed, Cancelled, Failed }
public sealed class OrderRuleException(string message) : Exception(message);
public sealed class Order
{
    private readonly List<OrderItem> _items = [];
    private Order() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid CartId { get; private set; }
    public Guid CartRevision { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string RequestFingerprint { get; private set; } = "";
    public OrderStatus Status { get; private set; }
    public string Currency { get; private set; } = "";
    public decimal Subtotal { get; private set; }
    public decimal DiscountTotal { get; private set; }
    public decimal PayableAmount { get; private set; }
    public decimal ShippingCost { get; private set; }
    public ShippingQuoteSnapshot? Shipping { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset PricedAtUtc { get; private set; }
    public OrderAddress? Address { get; private set; }
    public Guid Revision { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();

    public static Order Create(Guid userId, Guid cartId, Guid cartRevision, Guid key, string fingerprint, string currency,
        DateTimeOffset pricedAt, DateTimeOffset now, IReadOnlyList<OrderItem> items, AddressInput? address, ShippingQuoteSnapshot? shipping = null)
    {
        if (userId == Guid.Empty || cartId == Guid.Empty || cartRevision == Guid.Empty || key == Guid.Empty ||
            fingerprint.Length != 64 || pricedAt == default || items.Count is < 1 or > 100 ||
            items.Select(item => item.ProductVariantId).Distinct().Count() != items.Count)
            throw new OrderRuleException("Order identity or items are invalid.");
        var order = new Order { Id = Guid.NewGuid(), UserId = userId, CartId = cartId, CartRevision = cartRevision,
            IdempotencyKey = key, RequestFingerprint = fingerprint, Currency = MoneyRules.Currency(currency),
            Status = OrderStatus.Pending, CreatedAtUtc = now.ToUniversalTime(), UpdatedAtUtc = now.ToUniversalTime(),
            PricedAtUtc = pricedAt.ToUniversalTime(), Revision = Guid.NewGuid(), Address = address is null ? null : OrderAddress.Create(address) };
        foreach (var item in items) { item.Attach(order.Id); order._items.Add(item); }
        order.Subtotal = items.Sum(item => item.UnitPrice * item.Quantity);
        order.DiscountTotal = items.Sum(item => item.DiscountAmount);
        if (shipping is not null)
        {
            if (shipping.Currency != order.Currency || shipping.ShippingMethodId == Guid.Empty || shipping.Address is null ||
                !items.Any(item => item.ProductKind == "Physical") || address is not null)
                throw new OrderRuleException("Shipping selection does not match the order.");
            MoneyRules.Amount(shipping.Cost, order.Currency, true);
            order.ShippingCost = shipping.Cost; order.Shipping = shipping;
        }
        order.PayableAmount = items.Sum(item => item.LineTotal) + order.ShippingCost;
        // Shared amount bounds and currency precision apply to totals as well as units.
        MoneyRules.Amount(order.Subtotal, order.Currency);
        MoneyRules.Amount(order.DiscountTotal, order.Currency, true);
        MoneyRules.Amount(order.PayableAmount, order.Currency, true);
        if (order.PayableAmount != order.Subtotal - order.DiscountTotal + order.ShippingCost) throw new OrderRuleException("Order totals are inconsistent.");
        return order;
    }
    public void Transition(OrderStatus next, DateTimeOffset now)
    {
        var allowed = (Status, next) switch
        {
            (OrderStatus.Pending, OrderStatus.AwaitingPayment or OrderStatus.Cancelled or OrderStatus.Failed) => true,
            (OrderStatus.AwaitingPayment, OrderStatus.Paid or OrderStatus.Cancelled or OrderStatus.Failed) => true,
            (OrderStatus.Paid, OrderStatus.Processing) => true,
            (OrderStatus.Processing, OrderStatus.Shipped) => true,
            (OrderStatus.Shipped, OrderStatus.Completed) => true,
            _ => false
        };
        if (!allowed) throw new OrderRuleException("Order status transition is not allowed.");
        Status = next; UpdatedAtUtc = now.ToUniversalTime(); Revision = Guid.NewGuid();
    }
    public OrderDto Dto() => new(Id, Status.ToString(), Currency, Subtotal, DiscountTotal, PayableAmount, CreatedAtUtc,
        UpdatedAtUtc, PricedAtUtc, Address?.Dto(), Items.Select(item => item.Dto()).ToArray(), ShippingCost, Shipping);
}
public sealed class OrderItem
{
    private OrderItem() { }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductVariantId { get; private set; }
    public string Sku { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public string ProductKind { get; private set; } = "";
    public Guid PriceId { get; private set; }
    public Guid? DiscountId { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal UnitDiscount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal FinalUnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal { get; private set; }
    public static OrderItem Snapshot(Guid variantId, string sku, string name, string kind, PriceLineQuote quote, string currency)
    {
        if (variantId == Guid.Empty || variantId != quote.ProductVariantId || quote.PriceId == Guid.Empty || quote.Currency != currency ||
            string.IsNullOrWhiteSpace(sku) || sku.Length > 100 || string.IsNullOrWhiteSpace(name) || name.Length > 200 ||
            kind is not ("Physical" or "Digital") || quote.Quantity is < 1 or > 999)
            throw new OrderRuleException("Order item identity or quantity is invalid.");
        MoneyRules.Amount(quote.BaseUnitPrice, currency);
        MoneyRules.Amount(quote.UnitDiscountAmount, currency, true);
        if (quote.UnitDiscountAmount > quote.BaseUnitPrice || quote.FinalUnitPrice != quote.BaseUnitPrice - quote.UnitDiscountAmount ||
            quote.DiscountAmount != quote.UnitDiscountAmount * quote.Quantity || quote.TotalLineAmount != quote.FinalUnitPrice * quote.Quantity ||
            (quote.DiscountId is null && quote.UnitDiscountAmount != 0))
            throw new OrderRuleException("Order item pricing is inconsistent.");
        MoneyRules.Amount(quote.DiscountAmount, currency, true); MoneyRules.Amount(quote.TotalLineAmount, currency, true);
        return new OrderItem { Id = Guid.NewGuid(), ProductVariantId = variantId, Sku = sku, ProductName = name, ProductKind = kind,
            PriceId = quote.PriceId, DiscountId = quote.DiscountId, UnitPrice = quote.BaseUnitPrice, UnitDiscount = quote.UnitDiscountAmount,
            DiscountAmount = quote.DiscountAmount, FinalUnitPrice = quote.FinalUnitPrice, Quantity = quote.Quantity, LineTotal = quote.TotalLineAmount };
    }
    internal void Attach(Guid orderId) { if (OrderId != Guid.Empty) throw new OrderRuleException("An item snapshot cannot be reassigned."); OrderId = orderId; }
    public OrderItemDto Dto() => new(Id, ProductVariantId, Sku, ProductName, ProductKind, PriceId, DiscountId, UnitPrice, UnitDiscount,
        DiscountAmount, FinalUnitPrice, Quantity, LineTotal);
}
public sealed class OrderAddress
{
    private OrderAddress() { }
    public string Recipient { get; private set; } = "";
    public string Street { get; private set; } = "";
    public string City { get; private set; } = "";
    public string PostalCode { get; private set; } = "";
    public string CountryCode { get; private set; } = "";
    public static OrderAddress Create(AddressInput input)
    {
        if (new[] { input.Recipient, input.Street, input.City, input.PostalCode, input.CountryCode }.Any(string.IsNullOrWhiteSpace) ||
            input.Recipient.Length > 200 || input.Street.Length > 500 || input.City.Length > 100 || input.PostalCode.Length > 20 ||
            input.CountryCode.Length != 2 || !input.CountryCode.All(char.IsAsciiLetter))
            throw new OrderRuleException("Address snapshot is invalid.");
        return new() { Recipient = input.Recipient.Trim(), Street = input.Street.Trim(), City = input.City.Trim(),
            PostalCode = input.PostalCode.Trim(), CountryCode = input.CountryCode.ToUpperInvariant() };
    }
    public AddressDto Dto() => new(Recipient, Street, City, PostalCode, CountryCode);
}
public sealed class OrderAudit
{
    private OrderAudit() { }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; } = "";
    public DateTimeOffset AtUtc { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public static OrderAudit Record(Guid orderId, Guid actor, string action, DateTimeOffset at, string correlation) =>
        new() { Id = Guid.NewGuid(), OrderId = orderId, ActorId = actor, Action = action, AtUtc = at.ToUniversalTime(), CorrelationId = correlation };
}
