using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Quonfig.Sdk.Extensions.Logging;

/// <summary>
/// <see cref="ILoggingBuilder"/> extensions that wire <see cref="IQuonfig"/> into the
/// Microsoft.Extensions.Logging pipeline as a per-call log-level filter.
/// </summary>
public static class QuonfigLoggingBuilderExtensions
{
    /// <summary>
    /// Installs a <see cref="QuonfigLoggerProvider"/> that wraps every
    /// <see cref="ILoggerProvider"/> already registered on <paramref name="builder"/>.
    /// Should be called LAST in the logging setup chain — providers added after this call
    /// run alongside (not under) the Quonfig filter.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="quonfig">The Quonfig client to consult for log-level decisions.</param>
    /// <returns>The same builder for chaining.</returns>
    public static ILoggingBuilder AddQuonfigFilter(this ILoggingBuilder builder, IQuonfig quonfig)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(quonfig);
#else
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        if (quonfig is null) throw new ArgumentNullException(nameof(quonfig));
#endif

        // Snapshot the ILoggerProvider registrations present at call time. Remove them so
        // they don't show up as siblings to the Quonfig wrapper.
        var providerDescriptors = new List<ServiceDescriptor>();
        for (int i = builder.Services.Count - 1; i >= 0; i--)
        {
            var d = builder.Services[i];
            if (d.ServiceType != typeof(ILoggerProvider)) continue;
            // Defensive: don't double-wrap if AddQuonfigFilter is called twice.
            if (IsQuonfigProviderDescriptor(d)) continue;
            providerDescriptors.Add(d);
            builder.Services.RemoveAt(i);
        }
        providerDescriptors.Reverse();

        builder.Services.AddSingleton<ILoggerProvider>(sp =>
        {
            var wrapped = new ILoggerProvider[providerDescriptors.Count];
            for (int i = 0; i < providerDescriptors.Count; i++)
            {
                wrapped[i] = MaterializeProvider(sp, providerDescriptors[i]);
            }
            return new QuonfigLoggerProvider(quonfig, wrapped);
        });

        return builder;
    }

    private static bool IsQuonfigProviderDescriptor(ServiceDescriptor d)
    {
        if (d.ImplementationType is { } t && typeof(QuonfigLoggerProvider).IsAssignableFrom(t)) return true;
        if (d.ImplementationInstance is QuonfigLoggerProvider) return true;
        return false;
    }

    private static ILoggerProvider MaterializeProvider(IServiceProvider sp, ServiceDescriptor d)
    {
        if (d.ImplementationInstance is ILoggerProvider inst)
        {
            return inst;
        }
        if (d.ImplementationFactory is not null)
        {
            return (ILoggerProvider)d.ImplementationFactory(sp);
        }
        if (d.ImplementationType is not null)
        {
            return (ILoggerProvider)ActivatorUtilities.CreateInstance(sp, d.ImplementationType);
        }
        throw new InvalidOperationException(
            $"Cannot materialize ILoggerProvider from ServiceDescriptor with no instance, factory, or type.");
    }
}
