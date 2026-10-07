using Demo.Shop;
using SyncLane;

namespace Demo;

/// <summary>Loads the product list of an account. The list has no dates, so it is read from the start every time.</summary>
internal sealed class ProductHandler(PretendShop shop, DemoData data) : IJobHandler
{
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        var page = await shop.GetProductsAsync(job.Owner, job.Cursor, cancellationToken);
        await data.SaveProductsAsync(job.Owner, page.Items, cancellationToken);

        return new JobPage(page.NextCursor, page.Items.Count);
    }
}
