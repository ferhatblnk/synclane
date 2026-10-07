namespace Demo.Shop;

/// <summary>One page of a listing, with the cursor of the next page when there is one.</summary>
internal sealed record ShopPage<TItem>(IReadOnlyList<TItem> Items, string? NextCursor);
