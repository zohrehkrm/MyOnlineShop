namespace MyOnlineShop.Cart.Domain;

public sealed class CartRuleException(string message) : Exception(message);
public sealed class Cart
{
    public const int MaximumItemQuantity = 999;
    public const int MaximumItems = 100;
    private readonly List<CartItem> _items = [];
    private Cart() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public Guid Revision { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyList<CartItem> Items => _items.AsReadOnly();
    public static Cart Create(Guid userId, DateTimeOffset now)
    {
        if (userId == Guid.Empty) throw new CartRuleException("Cart owner is required.");
        return new Cart { Id = Guid.NewGuid(), UserId = userId, IsActive = true, CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(), Revision = Guid.NewGuid() };
    }
    public void Add(Guid variantId, int quantity, DateTimeOffset now)
    {
        EnsureActive(); ValidateQuantity(quantity);
        if (variantId == Guid.Empty) throw new CartRuleException("Product variant is required.");
        var existing = _items.SingleOrDefault(item => item.ProductVariantId == variantId);
        if (existing is not null) existing.SetQuantity(existing.Quantity + quantity, now);
        else
        {
            if (_items.Count >= MaximumItems) throw new CartRuleException("Cart item limit reached.");
            _items.Add(CartItem.Create(Id, variantId, quantity, now));
        }
        Touch(now);
    }
    public void UpdateQuantity(Guid itemId, int quantity, DateTimeOffset now)
    { EnsureActive(); Item(itemId).SetQuantity(quantity, now); Touch(now); }
    public void Remove(Guid itemId, DateTimeOffset now)
    { EnsureActive(); _items.Remove(Item(itemId)); Touch(now); }
    public void Clear(DateTimeOffset now)
    { EnsureActive(); _items.Clear(); Touch(now); }
    public CartItem Item(Guid id) => _items.SingleOrDefault(item => item.Id == id) ?? throw new CartRuleException("Cart item does not belong to this cart.");
    public static void ValidateQuantity(int quantity)
    {
        if (quantity is < 1 or > MaximumItemQuantity) throw new CartRuleException($"Cart quantity must be between 1 and {MaximumItemQuantity}.");
    }
    private void EnsureActive() { if (!IsActive) throw new CartRuleException("Cart is inactive."); }
    private void Touch(DateTimeOffset now) { UpdatedAtUtc = now.ToUniversalTime(); Revision = Guid.NewGuid(); }
}
public sealed class CartItem
{
    private CartItem() { }
    public Guid Id { get; private set; }
    public Guid CartId { get; private set; }
    public Guid ProductVariantId { get; private set; }
    public int Quantity { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    internal static CartItem Create(Guid cartId, Guid variantId, int quantity, DateTimeOffset now)
    {
        var value = new CartItem { Id = Guid.NewGuid(), CartId = cartId, ProductVariantId = variantId, CreatedAtUtc = now.ToUniversalTime() };
        value.SetQuantity(quantity, now); return value;
    }
    internal void SetQuantity(int quantity, DateTimeOffset now)
    { Cart.ValidateQuantity(quantity); Quantity = quantity; UpdatedAtUtc = now.ToUniversalTime(); }
}
