using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Co.Revington.Sightline.Server;

internal sealed record TryHeader(string Name, string Value);

internal sealed record TryRequest(string Url, string Method, List<TryHeader>? Headers, string? Body);

/// Sends a request from the page to an app on this machine and brings the answer back. The page
/// cannot do this itself: the browser does not let it read answers from another port.
/// Every request carries a W3C traceparent, so the app's report of it can be matched to the answer.
internal static class TryProxy
{
    const int MaxBody = 512 * 1024;

    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // Development certificates are often not trusted by this process. Only local apps are ever called.
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Connection", "Transfer-Encoding", "traceparent" };

    public static async Task<object> SendAsync(TryRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !RuntimeRegistry.IsLocal(uri))
            return new { error = "Requests can only go to an app on this machine (http://localhost:<port>/...)." };

        var method = new HttpMethod(string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim().ToUpperInvariant());
        var traceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var spanId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

        using var message = new HttpRequestMessage(method, uri);
        message.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{spanId}-01");

        string? contentType = null;
        foreach (var h in request.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(h.Name) || Skipped.Contains(h.Name.Trim())) continue;
            if (h.Name.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = h.Value.Trim();
            else message.Headers.TryAddWithoutValidation(h.Name.Trim(), h.Value);
        }

        if (!string.IsNullOrEmpty(request.Body) && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType ?? "application/json", out var parsed)
                ? parsed : new MediaTypeHeaderValue("application/json");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await ReadAsync(response, ct);
            sw.Stop();

            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(h => new { name = h.Key, value = string.Join(", ", h.Value) })
                .ToList();
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            var text = IsText(type) || bytes.Length == 0
                ? Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, MaxBody)))
                : $"({type}, {bytes.Length:N0} bytes)";

            return new
            {
                status = (int)response.StatusCode,
                reason = response.ReasonPhrase,
                ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                headers,
                contentType = type,
                body = text,
                truncated = bytes.Length > MaxBody,
                traceId,
            };
        }
        catch (HttpRequestException ex)
        {
            return new { error = $"Could not reach {uri.GetLeftPart(UriPartial.Authority)}: {ex.Message} Is the app running?", traceId };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new { error = $"The app did not answer within {Http.Timeout.TotalSeconds:0} seconds.", traceId };
        }
    }

    static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0 && buffer.Length <= MaxBody) buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    static bool IsText(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("problem", StringComparison.OrdinalIgnoreCase);
}
