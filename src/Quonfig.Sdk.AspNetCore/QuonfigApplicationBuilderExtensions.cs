using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Quonfig.Sdk.AspNetCore;

/// <summary>
/// <see cref="IApplicationBuilder"/> extensions for plugging Quonfig per-request context
/// binding into the ASP.NET Core pipeline.
/// </summary>
public static class QuonfigApplicationBuilderExtensions
{
    /// <summary>
    /// Installs middleware that calls <paramref name="contextBuilder"/> on each request to
    /// populate a <see cref="ContextSet"/> from the current <see cref="HttpContext"/>,
    /// then binds it to the registered <see cref="IQuonfig"/> singleton. The resulting
    /// <see cref="IBoundQuonfig"/> is published on <see cref="HttpContext.Items"/> so the
    /// scoped DI registration (added by <see cref="QuonfigServiceCollectionExtensions.AddQuonfig"/>)
    /// resolves it.
    /// </summary>
    public static IApplicationBuilder UseQuonfigContext(
        this IApplicationBuilder app,
        Action<HttpContext, ContextSet> contextBuilder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(contextBuilder);

        return app.UseMiddleware<QuonfigRequestContextMiddleware>(contextBuilder);
    }
}
