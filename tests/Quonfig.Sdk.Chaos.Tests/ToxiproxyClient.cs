using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Tiny client for the toxiproxy admin API (default <c>http://127.0.0.1:8474</c>). Just enough
/// to (re)create proxies, list/delete toxics, enable/disable proxies, and add new toxics.
/// Mirrors sdk-java's <c>ToxiproxyClient</c> shape.
/// </summary>
internal sealed class ToxiproxyClient : IDisposable
{
    private readonly string _base;
    private readonly HttpClient _http;

    public ToxiproxyClient(string baseUrl)
    {
        if (baseUrl is null) throw new ArgumentNullException(nameof(baseUrl));
        _base = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl.Substring(0, baseUrl.Length - 1)
            : baseUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task PingAsync()
    {
        using var resp = await _http.GetAsync(_base + "/version").ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new IOException("toxiproxy /version: HTTP " + (int)resp.StatusCode);
        }
    }

    public async Task UpsertProxyAsync(string name, string listen, string upstream)
    {
        try
        {
            using var del = await _http.DeleteAsync(_base + "/proxies/" + name).ConfigureAwait(false);
            // 404 is fine.
        }
        catch (HttpRequestException) { /* ignore */ }
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["listen"] = listen,
            ["upstream"] = upstream,
            ["enabled"] = true,
        };
        using var resp = await PostAsync("/proxies", body).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new IOException($"create proxy {name}: HTTP {(int)resp.StatusCode} — {msg}");
        }
    }

    public async Task ClearToxicsAsync(string proxy)
    {
        using var resp = await _http.GetAsync(_base + "/proxies/" + proxy + "/toxics").ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode)
        {
            throw new IOException($"list toxics {proxy}: HTTP {(int)resp.StatusCode}");
        }
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            if (!t.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String) continue;
            var n = nm.GetString();
            if (string.IsNullOrEmpty(n)) continue;
            try
            {
                using var del = await _http.DeleteAsync(_base + "/proxies/" + proxy + "/toxics/" + n).ConfigureAwait(false);
                // best-effort
            }
            catch (HttpRequestException) { /* best-effort */ }
        }
    }

    public async Task SetEnabledAsync(string proxy, bool enabled)
    {
        var body = new Dictionary<string, object?> { ["enabled"] = enabled };
        using var resp = await PostAsync("/proxies/" + proxy, body).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new IOException($"set {proxy} enabled={enabled}: HTTP {(int)resp.StatusCode} — {msg}");
        }
    }

    public async Task AddToxicAsync(string proxy, string name, string type, string stream, Dictionary<string, object?> attributes)
    {
        var s = string.IsNullOrEmpty(stream) ? "downstream" : stream;
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["type"] = type,
            ["stream"] = s,
            ["attributes"] = attributes,
        };
        using var resp = await PostAsync("/proxies/" + proxy + "/toxics", body).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new IOException($"add toxic {proxy}/{name}: HTTP {(int)resp.StatusCode} — {msg}");
        }
    }

    public async Task RemoveToxicAsync(string proxy, string name)
    {
        using var resp = await _http.DeleteAsync(_base + "/proxies/" + proxy + "/toxics/" + name).ConfigureAwait(false);
        if (resp.StatusCode != System.Net.HttpStatusCode.NoContent
            && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            throw new IOException($"delete toxic {proxy}/{name}: HTTP {(int)resp.StatusCode}");
        }
    }

    private Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, object?> body)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return _http.PostAsync(_base + path, content);
    }

    public void Dispose() => _http.Dispose();
}
