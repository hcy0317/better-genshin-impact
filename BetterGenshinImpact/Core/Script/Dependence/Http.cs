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
using BetterGenshinImpact.GameTask;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Script.Dependence;

public class Http
{
    private readonly ILogger<Http> _logger = App.GetLogger<Http>();

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
            _logger.LogDebug($"[HTTP] 检查URL {url} 是否符合: {pattern}");
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            return regex.IsMatch(url);
        }))
        {
            return;
        }
        throw new UnauthorizedAccessException($"当前JS脚本不允许请求此URL: {url}，请在脚本的manifest.json中配置http_allowed_urls，当前允许的URL列表: [{string.Join(", ", allowedUrls)}]");
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
        _logger.LogDebug($"[HTTP] 发送HTTP请求: {method} {url} Body: {(body != null ? body : "null")} Headers: {(headersJson != null ? headersJson : "null")}");
        CheckHttpPermission(url);

        using var request = CreateRequest(method, url, body, headersJson);
        using var httpClient = new HttpClient();
        using var response = await httpClient.SendAsync(request);
        return new HttpReponse
        {
            status_code = (int)response.StatusCode,
            headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.First()),
            body = await response.Content.ReadAsStringAsync(),
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
