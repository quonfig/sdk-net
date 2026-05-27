using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Quonfig.Sdk.AspNetCore;

/// <summary>
/// <see cref="IHostedService"/> that drives <see cref="IQuonfig"/>'s lifecycle in line with
/// the ASP.NET Core host: <c>StartAsync</c> awaits the initial envelope load,
/// <c>StopAsync</c> stops the background workers.
/// </summary>
internal sealed class QuonfigHostedService : IHostedService
{
    private readonly IQuonfig _quonfig;

    public QuonfigHostedService(IQuonfig quonfig)
    {
        _quonfig = quonfig;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _quonfig.InitAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _quonfig.CloseAsync();
}
