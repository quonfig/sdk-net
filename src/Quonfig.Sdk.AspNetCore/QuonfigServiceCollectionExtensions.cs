using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Quonfig.Sdk.AspNetCore;

/// <summary>
/// <see cref="IServiceCollection"/> extensions that register the Quonfig client and
/// supporting services for ASP.NET Core applications.
/// </summary>
public static class QuonfigServiceCollectionExtensions
{
    /// <summary>
    /// Marker used by <see cref="QuonfigRequestContextMiddleware"/> to publish the
    /// per-request <see cref="IBoundQuonfig"/> on <see cref="HttpContext.Items"/>.
    /// </summary>
    internal const string BoundQuonfigItemKey = "Quonfig.BoundQuonfig";

    /// <summary>
    /// Registers <see cref="IQuonfig"/> as a singleton, configures it from
    /// <paramref name="configure"/>, and wires a hosted service that awaits
    /// <see cref="IQuonfig.InitAsync"/> on application start and
    /// <see cref="IQuonfig.CloseAsync"/> on shutdown. Also registers
    /// <see cref="IBoundQuonfig"/> as a scoped service backed by the per-request
    /// context populated by <see cref="QuonfigApplicationBuilderExtensions.UseQuonfigContext"/>;
    /// when the middleware isn't installed the bound view is empty.
    /// </summary>
    public static IServiceCollection AddQuonfig(
        this IServiceCollection services,
        Action<QuonfigOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<QuonfigOptions>().Configure(configure);
        services.AddHttpContextAccessor();

        services.TryAddSingleton<IQuonfig>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<QuonfigOptions>>().Value;
            return new Sdk.Quonfig(opts);
        });

        services.AddHostedService<QuonfigHostedService>();

        services.TryAddScoped<IBoundQuonfig>(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (http is not null
                && http.Items.TryGetValue(BoundQuonfigItemKey, out var stored)
                && stored is IBoundQuonfig bound)
            {
                return bound;
            }
            // No middleware installed (or call outside an HTTP request) — empty context.
            return sp.GetRequiredService<IQuonfig>().WithContext(new ContextSet());
        });

        return services;
    }
}
