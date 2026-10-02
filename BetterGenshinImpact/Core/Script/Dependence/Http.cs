using BetterGenshinImpact.Core.Script.Utils;
using System;
using System.IO;
using System.Threading.Tasks;
using OpenCvSharp;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Script.Dependence;

public class Http
{
    // 进程内稳定的接口身份；不落盘路径，也不输出可离线枚举敏感路径的裸摘要。
    private static readonly byte[] DiagnosticEndpointKey = RandomNumberGenerator.GetBytes(32);
    private readonly ILogger<Http> _logger = App.GetLogger<Http>();
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private void CheckHttpPermission(string url)
    {
        var currentProject = TaskContext.Instance().CurrentScriptProject;
        if (!currentProject?.AllowJsHTTP ?? false)
        {
            throw new UnauthorizedAccessException("当前JS脚本不允许使用HTTP请求，请在调度器通用设置中启用“JS HTTP权限”");
        }
        var allowedUrls = currentProject?.Project?.Manifest.HttpAllowedUrls ?? [];
        if (allowedUrls.Length == 0)
        {
            throw new UnauthorizedAccessException("当前JS脚本没有配置允许请求的URL，请在脚本的manifest.json中配置http_allowed_urls");
        }
        if (allowedUrls.Any(allowedUrl =>
        {
            // fuzzy match
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(allowedUrl).Replace("\\*", ".*") + "$";
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            return regex.IsMatch(url);
        }))
        {
            return;
        }
        throw new UnauthorizedAccessException($"当前JS脚本不允许请求 {SafeAddress(url)}，请检查manifest.json中的http_allowed_urls");
    }

    public class HttpReponse
    {
        public int status_code { get; set; }
        public Dictionary<string, string> headers { get; set; } = new();
        public string body { get; set; } = "";
    }


    /// <summary>
    /// 执行HTTP请求
    /// </summary>
    /// <param name="method">HTTP方法</param>
    /// <param name="url">请求URL</param>
    /// <param name="body">请求体</param>
    /// <param name="headersJson">请求头，JSON格式</param>
    /// <returns></returns>
    public async Task<HttpReponse> Request(string method, string url, string? body = null, string? headersJson = null)
    {
        var ct = CancellationContext.Instance.GetTokenOrNone();
        ct.ThrowIfCancellationRequested();
        CheckHttpPermission(url);
        var requestId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        using var request = CreateRequest(method, url, body, headersJson);
        _logger.LogDebug("HTTP_REQUEST request={Request} method={Method} endpoint={Endpoint} phase=send", requestId, method, SafeAddress(url));
        try
        {
            var result = await SendAsync(SharedClient, request, ct, TimeSpan.FromSeconds(100));
            RecordResponseEvidence(requestId, url, result, Stopwatch.GetElapsedTime(started).TotalMilliseconds, _logger);
            _logger.LogDebug("HTTP_REQUEST request={Request} phase=complete status={Status} ms={Milliseconds:F1}",
                requestId, result.status_code, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception error)
        {
            try
            {
                DiagnosticEvidenceScope.Current?.RequestLatestWindow(IncidentKey(url), "http-failed",
                    $"requestId={requestId} endpoint={SafeAddress(url)} errorType={error.GetType().Name} " +
                    $"cancelled={ct.IsCancellationRequested} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} " +
                    "businessCode=unknown:no-response lockHolder=unknown:not-exposed-by-script-http-bridge", _logger,
                    changeKey: $"errorType={error.GetType().Name};cancelled={ct.IsCancellationRequested}");
            }
            catch { /* 取证失败不替换原始请求异常。 */ }
            _logger.LogDebug("HTTP_REQUEST request={Request} phase=failed errorType={Type} cancelled={Cancelled} ms={Milliseconds:F1}",
                requestId, error.GetType().Name, ct.IsCancellationRequested, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    internal static void RecordResponseEvidence(Guid requestId, string url, HttpReponse response, double elapsedMs, ILogger logger)
    {
        try
        {
            long? code = null;
            var busy = response.status_code is 429 or 503;
            var codeState = response.body.Length > 65536 ? "payload-too-large" : "no-numeric-code";
            if (response.body.Length <= 65536)
            {
                try
                {
                    using var json = JsonDocument.Parse(response.body);
                    if (json.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var name in new[] { "retcode", "code" })
                            if (json.RootElement.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var value))
                            { code = value; break; }
                        foreach (var name in new[] { "message", "msg" })
                            if (json.RootElement.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String)
                                busy |= field.GetString()?.Contains("系统繁忙", StringComparison.Ordinal) == true;
                    }
                }
                catch (JsonException) { codeState = "non-json-response"; }
            }
            var detail = FormattableString.Invariant($"requestId={requestId} endpoint={SafeAddress(url)} httpStatus={response.status_code} businessCode={code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown:" + codeState} busy={busy} elapsedMs={elapsedMs:F1} lockHolder=unknown:not-exposed-by-script-http-bridge lockConflict=unknown:not-exposed-by-script-http-bridge");
            logger.LogDebug("HTTP_RESPONSE_EVIDENCE {Detail}", detail);
            if (busy || response.status_code >= 400)
            {
                logger.LogWarning("HTTP_FAILURE_EVIDENCE {Detail}", detail);
                DiagnosticEvidenceScope.Current?.RequestLatestWindow(IncidentKey(url), "http-failed", detail, logger,
                    changeKey: $"httpStatus={response.status_code};businessCode={code};busy={busy}");
            }
        }
        catch { /* 不读取完整请求/响应到日志，不重试或改变接口返回值。 */ }
    }

    private static string IncidentKey(string url)
    {
        var endpoint = Uri.TryCreate(url, UriKind.Absolute, out var address)
            ? address.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "invalid-url";
        return "http:" + Convert.ToHexString(HMACSHA256.HashData(DiagnosticEndpointKey, Encoding.UTF8.GetBytes(endpoint)));
    }

    internal static string SafeAddress(string url) => Uri.TryCreate(url, UriKind.Absolute, out var address)
        // 任意脚本URL没有受控路由模板，路径也可能包含token/用户ID；只记录服务来源。
        ? address.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).TrimEnd('/') + "/[redacted-path]"
        : "invalid-url";

    internal static async Task<HttpReponse> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken ct, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new HttpReponse
        {
            status_code = (int)response.StatusCode,
            headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.First()),
            body = body
        };
    }

    internal static HttpRequestMessage CreateRequest(string method, string url, string? body, string? headersJson)
    {
        var dictHeaders = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(headersJson))
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (headers != null)
                {
                    dictHeaders = headers;
                }
            }
            catch (JsonException)
            {
                throw new ArgumentException("Headers JSON格式错误");
            }
        }

        // header全部小写
        // 可选认证未配置时，旧脚本会生成 { "": "" }；只忽略完全为空的占位项。
        // 空名称带有值仍是错误，不能静默丢弃可能必需的认证信息。
        dictHeaders = dictHeaders
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) || !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(kvp => kvp.Key.ToLowerInvariant(), kvp => kvp.Value);

        // 提前取出来Content-Type，防止被覆盖
        string contentType = "application/json";
        if (dictHeaders.TryGetValue("content-type", out var ct))
        {
            contentType = ct;
            dictHeaders.Remove("content-type");
        }

        var request = new HttpRequestMessage(new HttpMethod(method), url);
        try
        {
            foreach (var header in dictHeaders)
                request.Headers.Add(header.Key, header.Value);
            request.Content = body == null ? null : new StringContent(body, Encoding.UTF8, contentType);
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }
}
