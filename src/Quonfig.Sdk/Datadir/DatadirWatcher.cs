using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quonfig.Sdk.Datadir;

/// <summary>
/// Watches a datadir workspace for filesystem changes and fires <c>onChange</c> once per debounced
/// burst. Uses <see cref="FileSystemWatcher"/> with <c>IncludeSubdirectories=true</c>, which under
/// the hood is native inotify on Linux, ReadDirectoryChangesW on Windows, and a kqueue/polling
/// hybrid on macOS (documented caveat — same as sdk-java's <c>WatchService</c>).
///
/// <para>Registration failures (missing path, read-only filesystem, immutable container) are
/// surfaced via the <c>onError</c> callback; <see cref="Start"/> returns <c>false</c> and the
/// watcher cleans up any partial state. The caller is expected to log and degrade — the SDK's
/// <c>Quonfig</c> client treats a watcher start failure as "auto-reload is off" without throwing.
/// </para>
///
/// <para>The watcher fires the debounced trigger only — the caller owns parse-then-swap: try to
/// reload the envelope, and on success swap in the new store; on failure keep serving the
/// previous envelope and do NOT fire <c>OnConfigChange</c>.</para>
///
/// <para>Mirrors sdk-java's <c>com.quonfig.sdk.datadir.DatadirWatcher</c> (qfg-mol-0kr / 3jq):
/// same API shape — <see cref="Start"/> returns bool, <see cref="DisposeAsync"/> is idempotent,
/// and bursts of filesystem events coalesce into a single <c>onChange</c> per debounce window.
/// Resolves the datadir to its real path at start so edits to the file a symlink points at are
/// detected (the common case for blue/green workspace flips).</para>
/// </summary>
public sealed class DatadirWatcher : IAsyncDisposable, IDisposable
{
    private readonly string _datadir;
    private readonly TimeSpan _debounce;
    private readonly Action _onChange;
    private readonly Action<Exception> _onError;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private FileSystemWatcher? _fsw;
    private Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>
    /// Constructs a watcher. Call <see cref="Start"/> to begin watching; the caller owns lifecycle
    /// and is expected to <see cref="DisposeAsync"/> on shutdown.
    /// </summary>
    /// <param name="datadir">Workspace directory path. Resolved to its real path on start.</param>
    /// <param name="debounce">Coalesce window for filesystem events. 200ms is a sensible default.</param>
    /// <param name="onChange">Invoked once per debounced burst. Runs on a thread-pool thread.</param>
    /// <param name="onError">Invoked when registration fails or the watcher reports an internal error.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger.Instance"/>.</param>
    public DatadirWatcher(
        string datadir,
        TimeSpan debounce,
        Action onChange,
        Action<Exception> onError,
        ILogger? logger = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(datadir);
        ArgumentNullException.ThrowIfNull(onChange);
        ArgumentNullException.ThrowIfNull(onError);
#else
        if (datadir is null) throw new ArgumentNullException(nameof(datadir));
        if (onChange is null) throw new ArgumentNullException(nameof(onChange));
        if (onError is null) throw new ArgumentNullException(nameof(onError));
#endif
        _datadir = datadir;
        _debounce = debounce;
        _onChange = onChange;
        _onError = onError;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Resolves the datadir to its real path (symlink-following), opens a
    /// <see cref="FileSystemWatcher"/> on that resolved path with recursive subdirectory
    /// monitoring, and arms the debounced trigger. Returns <c>false</c> on any registration
    /// failure — typical causes: the datadir does not exist, lives on a read-only filesystem,
    /// or runs inside an immutable container. On failure the <c>onError</c> callback is invoked
    /// with the cause and any partially-built state is cleaned up so <see cref="DisposeAsync"/>
    /// is still safe to call.
    /// </summary>
#pragma warning disable CA1031 // any exception during start is reported via onError; we never throw
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_fsw is not null) return true;

            try
            {
                string resolved = ResolveRealPath(_datadir);
                if (!Directory.Exists(resolved))
                {
                    throw new DirectoryNotFoundException($"datadir not found: {resolved}");
                }

                var watcher = new FileSystemWatcher(resolved)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size
                                   | NotifyFilters.CreationTime,
                    EnableRaisingEvents = false,
                };
                watcher.Created += OnFsEvent;
                watcher.Changed += OnFsEvent;
                watcher.Deleted += OnFsEvent;
                watcher.Renamed += OnFsRenamed;
                watcher.Error += OnFsError;
                watcher.EnableRaisingEvents = true;

                _debounceTimer = new Timer(OnDebounceFire, state: null, Timeout.Infinite, Timeout.Infinite);
                _fsw = watcher;
                return true;
            }
            catch (Exception ex)
            {
                _onError(ex);
                // Best-effort cleanup of anything we built.
                _fsw?.Dispose();
                _fsw = null;
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                return false;
            }
        }
    }
#pragma warning restore CA1031

    private static string ResolveRealPath(string path)
    {
#if NET8_0_OR_GREATER
        // ResolveLinkTarget returns null when the path is not a symlink; in that case fall back to
        // GetFullPath. When it is a symlink, follow it once (final: false) — same shape as Java's
        // Path.toRealPath() default.
        if (Directory.Exists(path))
        {
            try
            {
                var info = new DirectoryInfo(path);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is DirectoryInfo dt) return dt.FullName;
            }
            catch (IOException)
            {
                // Fall through to GetFullPath — best-effort symlink resolution.
            }
        }
#endif
        return Path.GetFullPath(path);
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void OnFsRenamed(object sender, RenamedEventArgs e) => Schedule();

    private void OnFsError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        if (ex is not null) _onError(ex);
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Re-arm the timer for `_debounce` from now. If a previous arming was pending,
            // Change cancels it and reschedules — that's the coalescing behavior.
            _debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

#pragma warning disable CA1031 // _onChange is application code; any exception is reported through _onError
    private void OnDebounceFire(object? _)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        try
        {
            _onChange();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }
#pragma warning restore CA1031

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        FileSystemWatcher? fsw;
        Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            fsw = _fsw;
            timer = _debounceTimer;
            _fsw = null;
            _debounceTimer = null;
        }
        if (fsw is not null)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        }
        if (timer is not null)
        {
#if NET8_0_OR_GREATER
            await timer.DisposeAsync().ConfigureAwait(false);
#else
            timer.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
#endif
        }
        else
        {
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Synchronous disposal. Prefer <see cref="DisposeAsync"/> when the caller is in an async
    /// context. Important: this path does NOT wait for an in-flight debounced callback to
    /// finish — the old implementation did (via <c>GetAwaiter().GetResult()</c> on
    /// <see cref="DisposeAsync"/>), which could deadlock if the caller held a lock the callback
    /// was also blocked on (qfg-zp7i.21). Pending callbacks observe the <c>_disposed</c> flag and
    /// return without firing <c>onChange</c>.
    /// </summary>
    public void Dispose()
    {
        FileSystemWatcher? fsw;
        Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            fsw = _fsw;
            timer = _debounceTimer;
            _fsw = null;
            _debounceTimer = null;
        }
        if (fsw is not null)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        }
        timer?.Dispose();
    }
}
