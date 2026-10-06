using System.Security.Cryptography;
using System.Text.Json;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Shipping.Contracts;
using OrderAggregate = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Order.Application;

public sealed class CheckoutCommands(IOrderStore store, IOrderUnitOfWork unit, ICheckoutCart carts,
    ICatalogVariantReferences catalog, IPricingCalculation pricing, IInventoryAvailability inventory,
    TimeProvider clock, IRequestContext request, IOutboxWriter outbox, IEnumerable<IShippingQuotes> shippingQuotes) : ICheckoutCommands
{
    public async Task<OrderDto> CreateAsync(Guid userId, CheckoutInput input, CancellationToken ct)
    {
        OrderException.User(userId);
        if (input.IdempotencyKey == Guid.Empty) throw OrderException.Invalid("An idempotency key is required.");
        string currency;
        try { currency = MoneyRules.Currency(input.Currency); } catch (MoneyRuleException error) { throw OrderException.Invalid(error.Message); }
        var coupon = input.CouponCode?.Trim().ToUpperInvariant();
        if (coupon is not null && (coupon.Length is < 1 or > 64 || coupon.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')))
            throw OrderException.Invalid("Coupon code is invalid.");
        // Structured serialization prevents ambiguous concatenation; no PII is logged.
        AddressDto? address;
        try { address = input.Address is null ? null : OrderAddress.Create(input.Address).Dto(); }
        catch (OrderRuleException error) { throw OrderException.Invalid(error.Message); }
        if (input.Shipping is not null && input.Address is not null) throw OrderException.Invalid("Use Shipping.Address for a shipping selection, not both address inputs.");
        // Preserve pre-Shipping request fingerprints for existing idempotent checkouts.
        var fingerprintBytes = input.Shipping is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { Currency = currency, Coupon = coupon, Address = address })
            : JsonSerializer.SerializeToUtf8Bytes(new { Currency = currency, Coupon = coupon, Address = address, Shipping = input.Shipping });
        var fingerprint = Convert.ToHexString(SHA256.HashData(fingerprintBytes));
        return await unit.ExecuteAsync(true, async token =>
        {
            await unit.LockCheckoutAsync(userId, token);
            var existing = await store.GetCheckoutAsync(userId, input.IdempotencyKey, token);
            if (existing is not null)
            {
                if (existing.RequestFingerprint != fingerprint) throw OrderException.Conflict("Idempotency key has already been used for a different request.");
                return existing.Dto();
            }
            var snapshot = await carts.GetAsync(userId, token); var cart = snapshot.Cart;
            if (cart.Id is null || snapshot.Revision == Guid.Empty || !cart.IsActive || cart.Items.Count is < 1 or > 100 ||
                cart.Items.Any(item => item.ProductVariantId == Guid.Empty || item.Quantity is < 1 or > 999) ||
                cart.Items.Select(item => item.ProductVariantId).Distinct().Count() != cart.Items.Count)
                throw OrderException.Invalid("Cart is empty or contains invalid items.");
            var ids = cart.Items.Select(item => item.ProductVariantId).ToArray();
            var variants = (await catalog.GetManyAsync(ids, token)).ToDictionary(value => value.Id);
            if (variants.Count != ids.Length || variants.Values.Any(value => !value.IsActive))
                throw OrderException.Invalid("Variant is missing or not purchasable.");
            var quote = await pricing.CalculateAsync(cart.Items.Select(item => new PriceLineRequest(item.ProductVariantId, item.Quantity)).ToArray(), currency, coupon, token);
            if (quote.Currency != currency || quote.Lines.Count != cart.Items.Count || quote.Lines.Select(line => line.ProductVariantId).Distinct().Count() != cart.Items.Count)
                throw OrderException.Invalid("Pricing result is inconsistent.");
            var lines = quote.Lines.ToDictionary(line => line.ProductVariantId);
            var items = cart.Items.Select(item =>
            {
                if (!lines.TryGetValue(item.ProductVariantId, out var line) || line.Quantity != item.Quantity)
                    throw OrderException.Invalid("Pricing quantities do not match Cart.");
                var variant = variants[item.ProductVariantId];
                return OrderItem.Snapshot(variant.Id, variant.Sku, variant.ProductName ?? "", variant.ProductKind, line, currency);
            }).ToArray();
            // Digital products do not consume physical warehouse stock.
            var physical = variants.Values.Where(value => value.ProductKind == "Physical").Select(value => value.Id).ToArray();
            var availability = (physical.Length == 0 ? [] : await inventory.GetAsync(physical, token)).ToDictionary(value => value.ProductVariantId);
            if (items.Any(item => item.ProductKind == "Physical" && (!availability.TryGetValue(item.ProductVariantId, out var stock) || stock.AvailableQuantity < item.Quantity)))
                throw OrderException.Conflict("Insufficient current inventory. Availability is not reserved.");
            ShippingQuoteSnapshot? shipping = null;
            if (input.Shipping is not null)
            {
                if (physical.Length == 0) throw OrderException.Invalid("Digital-only orders do not require shipping.");
                var calculator = shippingQuotes.SingleOrDefault() ?? throw OrderException.Invalid("Shipping quotation is unavailable.");
                shipping = await calculator.CalculateAsync(new() { ShippingMethodId = input.Shipping.ShippingMethodId, Currency = currency,
                    Address = input.Shipping.Address }, token);
                if (shipping.ShippingMethodId != input.Shipping.ShippingMethodId || shipping.Currency != currency)
                    throw OrderException.Invalid("Shipping quotation does not match the selection.");
            }
            var order = OrderAggregate.Create(userId, cart.Id.Value, snapshot.Revision, input.IdempotencyKey, fingerprint,
                currency, quote.CalculatedAtUtc, clock.GetUtcNow(), items, input.Address, shipping);
            order.Transition(OrderStatus.AwaitingPayment, clock.GetUtcNow());
            store.Add(order);
            store.Audit(OrderAudit.Record(order.Id, userId, "CheckoutCreated", clock.GetUtcNow(), request.CorrelationId));
            await carts.ClearAsync(userId, cart.Id.Value, snapshot.Revision, token);
            await outbox.EnqueueAsync(new OrderCreatedIntegrationEventV1(order.Id, order.CreatedAtUtc, order.Id, order.PayableAmount, order.Currency), request.CorrelationId, token);
            return order.Dto();
        }, ct);
    }
}
