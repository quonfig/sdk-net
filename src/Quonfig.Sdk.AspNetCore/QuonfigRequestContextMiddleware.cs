using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Quonfig.Sdk.AspNetCore;

/// <summary>
/// Middleware that builds a per-request <see cref="ContextSet"/> via a user-supplied
/// callback, binds it onto the singleton <see cref="IQuonfig"/>, and publishes the bound
/// view onto <see cref="HttpContext.Items"/> so the scoped <see cref="IBoundQuonfig"/>
/// resolution picks it up.
/// </summary>
internal sealed class QuonfigRequestContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Action<HttpContext, ContextSet> _contextBuilder;
    private readonly IQuonfig _quonfig;

    public QuonfigRequestContextMiddleware(
        RequestDelegate next,
        Action<HttpContext, ContextSet> contextBuilder,
        IQuonfig quonfig)
    {
        _next = next;
        _contextBuilder = contextBuilder;
        _quonfig = quonfig;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var ctxSet = new ContextSet();
        _contextBuilder(context, ctxSet);
        var bound = _quonfig.WithContext(ctxSet);
        context.Items[QuonfigServiceCollectionExtensions.BoundQuonfigItemKey] = bound;
        return _next(context);
    }
}
