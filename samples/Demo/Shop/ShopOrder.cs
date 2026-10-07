namespace Demo.Shop;

/// <summary>An order as the pretend shop returns it.</summary>
internal sealed record ShopOrder(string Id, DateTimeOffset PlacedAt, decimal Total);
