using Demo.Shop;
using SyncLane;

namespace Demo;

/// <summary>Loads the orders of an account that fall inside the window of the job.</summary>
internal sealed class OrderHandler(PretendShop shop, DemoData data) : IJobHandler
{
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.WindowStart is not { } from || job.WindowEnd is not { } to)
            throw new JobAbandonedException("An orders job needs a window.");

        var page = await shop.GetOrdersAsync(job.Owner, from, to, job.Cursor, cancellationToken);
        await data.SaveOrdersAsync(job.Owner, page.Items, cancellationToken);

        return new JobPage(page.NextCursor, page.Items.Count);
    }
}
