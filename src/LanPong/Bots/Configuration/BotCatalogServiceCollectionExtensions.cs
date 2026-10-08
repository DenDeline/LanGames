using Microsoft.Extensions.Options;
using LanPong.Bots.Catalog;
using LanPong.Bots.Runtime;
using LanPong.Bots.Strategies;

namespace LanPong.Bots.Configuration;

internal static class BotCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredBots(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBotCatalog(configuration);
        services.AddSingleton<IBotStrategyFactory, TrackerBotStrategyFactory>();
        services.AddSingleton<IBotStrategyFactory, OnnxBotStrategyFactory>();
        services.AddSingleton<BotRuntime>();
        return services;
    }

    public static IServiceCollection AddBotCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new BotStrategyDescriptor(BotStrategyDescriptor.TrackerId, BotSettingsKind.Tracker));
        services.AddSingleton(new BotStrategyDescriptor(BotStrategyDescriptor.OnnxId, BotSettingsKind.Onnx));
        services.AddSingleton<BotStrategyRegistry>();
        services.AddSingleton<IValidateOptions<BotsOptions>, BotsOptionsValidator>();
        services.AddOptions<BotsOptions>()
            // Bind through a concrete generated call without registering reload/change-token sources.
            .Configure(options => configuration.GetSection(BotsOptions.SectionName)
                .Bind(options, binder => binder.ErrorOnUnknownConfiguration = true))
            .ValidateOnStart();
        services.AddSingleton<BotCatalog>();
        return services;
    }
}
