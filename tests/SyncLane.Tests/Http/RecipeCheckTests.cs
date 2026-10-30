using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class RecipeCheckTests
{
    private const string Orders = """{ "name": "test/orders", "request": { "url": "https://api.example.com/orders" } }""";

    [Fact]
    public async Task A_missing_or_misspelt_recipe_stops_the_host_before_the_database_is_touched()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = "Host=nowhere.invalid;Username=nobody;Timeout=1";
            options.CreateSchema = false;
            options.AddQueue("shops", queue => queue.Windowed<HttpRecipeHandler>("orders", TimeSpan.FromDays(30), TimeSpan.FromDays(7)));
        });
        builder.Services.AddSyncLaneHttp(http => http.Recipe("order", Orders).WriteTo((_, _) => Task.CompletedTask));
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => host.StartAsync());

        Assert.Equal([HttpErrorCodes.RecipeMissing, HttpErrorCodes.RecipeUnused], thrown.Errors.Select(error => error.Code));
        Assert.Contains("'orders' on queue 'shops'", thrown.Errors[0].Message);
        Assert.Contains("'order'", thrown.Errors[1].Message);
    }

    [Fact]
    public async Task Matching_recipes_and_job_types_pass()
    {
        var options = new SyncLaneOptions().AddQueue("shops", queue => queue.Handle<HttpRecipeHandler>("orders"));
        var http = new HttpSourceOptions();
        http.Recipe("orders", Orders).WriteTo((_, _) => Task.CompletedTask);

        await new RecipeCheck([options], new RecipeRegistry(http.Bindings)).StartingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_the_engine_registered_there_is_nothing_to_compare()
    {
        await new RecipeCheck([], new RecipeRegistry([])).StartingAsync(CancellationToken.None);
    }
}
