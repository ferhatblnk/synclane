using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SyncLane.Http;

/// <summary>Registers the HTTP source.</summary>
public static class HttpSourceServiceCollectionExtensions
{
    /// <summary>
    /// Registers recipes and the handler that runs them. Declare the job types on their queues with
    /// <see cref="HttpRecipeHandler"/>, in <c>AddSyncLane</c>. Every problem with the settings and recipes is thrown here,
    /// together; a recipe without a declared job type, or the reverse, stops the application at start-up.
    /// </summary>
    public static IServiceCollection AddSyncLaneHttp(this IServiceCollection services, Action<HttpSourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HttpSourceOptions();
        configure(options);

        var errors = options.Validate();
        if (errors.Count > 0)
            throw new SyncLaneException(errors);

        var recipes = new RecipeRegistry(options.Bindings);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(recipes);

        foreach (var sink in options.Bindings.Select(binding => binding.SinkType).OfType<Type>().Distinct())
            services.TryAddScoped(sink);

        if (options.CredentialStore is { } store)
            services.AddScoped(typeof(ICredentialStore), store);

        foreach (var (name, signer) in options.Signers)
            services.AddKeyedScoped(typeof(IRequestSigner), name, signer);

        services.TryAddSingleton(provider => new OAuthTokens(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<HttpSourceOptions>(),
            provider.GetRequiredService<TimeProvider>()));

        // Registered with a factory because the constructor is internal. AddSyncLane registers every declared handler
        // by type; if it ran first, that registration is replaced, so validating the container never tries to build it.
        // If it runs later, it finds this registration and adds nothing.
        services.RemoveAll<HttpRecipeHandler>();
        services.AddScoped(provider => new HttpRecipeHandler(
            provider.GetRequiredService<RecipeRegistry>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpSource.ClientName),
            provider,
            provider.GetRequiredService<HttpSourceOptions>(),
            provider.GetRequiredService<TimeProvider>()));

        services.AddHttpClient(HttpSource.ClientName)
            .ConfigurePrimaryHttpMessageHandler(HttpSource.CreatePrimaryHandler)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();

        services.AddHostedService<RecipeCheck>();
        return services;
    }
}
