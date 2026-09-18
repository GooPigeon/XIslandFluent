using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using XIslandFluent.Models;

namespace XIslandFluent.Services;

public sealed class XIslandApiService : IDisposable
{
    public const string BaseUrl = "https://www.nmbxd1.com";
    public const string JsonApiBaseUrl = "https://api.nmb.best/api";
    public const string NoticeBaseUrl = "https://nmb.ovear.info";
    private const string UserAgent = "XIslandFluent/1.0 (Windows 11; native desktop client)";
    private readonly HttpClient _client;
    private readonly HttpClient _siteClient;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private AppSettings _settings;
    public string CdnBaseUrl { get; private set; } = "https://image.nmb.best/";

    public XIslandApiService(AppSettings settings)
    {
        _settings = settings;
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, AllowAutoRedirect = true };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _siteClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _siteClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _siteClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await GetAsync<List<CdnEntry>>("/getCDNPath", cancellationToken);
            var selected = entries.OrderByDescending(x => x.Rate).FirstOrDefault(x => Uri.TryCreate(x.Url, UriKind.Absolute, out _));
            if (selected is not null) CdnBaseUrl = selected.Url.EndsWith('/') ? selected.Url : selected.Url + "/";
        }
        catch { }
    }

    public Task<List<ForumCategory>> GetForumsAsync(CancellationToken ct = default) => GetAsync<List<ForumCategory>>("/getForumList", ct);
    public Task<List<TimelineInfo>> GetTimelinesAsync(CancellationToken ct = default) => GetAsync<List<TimelineInfo>>("/getTimelineList", ct);
    public Task<List<Post>> GetBoardAsync(int forumId, int page, CancellationToken ct = default) => GetAsync<List<Post>>($"/showf?id={forumId}&page={page}", ct);
    public Task<List<Post>> GetTimelineAsync(int timelineId, int page, CancellationToken ct = default) => GetAsync<List<Post>>($"/timeline?id={timelineId}&page={page}", ct);
    public Task<Post> GetThreadAsync(int threadId, int page, bool onlyPo = false, CancellationToken ct = default) => GetAsync<Post>($"/{(onlyPo ? "po" : "thread")}?id={threadId}&page={page}", ct);
    public Task<Post> GetReferenceAsync(int postId, CancellationToken ct = default) => GetAsync<Post>($"/ref?id={postId}", ct);
    public Task<List<FeedPost>> GetFeedAsync(string subscriptionId, int page, CancellationToken ct = default) => GetAsync<List<FeedPost>>($"/feed?uuid={Uri.EscapeDataString(subscriptionId)}&page={page}", ct);
    public Task<Notice> GetNoticeAsync(CancellationToken ct = default) => GetAsync<Notice>("/nmb-notice.json", ct, useSite: true, baseOverride: NoticeBaseUrl);

    public async Task<string> AddFeedAsync(string subscriptionId, int threadId, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["tid"] = threadId.ToString() });
        return await SendTextAsync(HttpMethod.Post, $"/addFeed?uuid={Uri.EscapeDataString(subscriptionId)}", form, ct, useSite: false);
    }

    public async Task<string> DeleteFeedAsync(string subscriptionId, int threadId, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["tid"] = threadId.ToString() });
        return await SendTextAsync(HttpMethod.Post, $"/delFeed?uuid={Uri.EscapeDataString(subscriptionId)}", form, ct, useSite: false);
    }

    public Task<string> CreateThreadAsync(int forumId, string title, string content, string? imagePath, bool watermark, CancellationToken ct = default) =>
        SendMultipartAsync("/Home/Forum/doPostThread.html", new Dictionary<string, string>
        {
            ["fid"] = forumId.ToString(), ["name"] = "无名氏", ["title"] = string.IsNullOrWhiteSpace(title) ? "无标题" : title,
            ["content"] = content, ["water"] = watermark ? "1" : "0"
        }, imagePath, true, ct);

    public Task<string> ReplyAsync(int threadId, string content, string? imagePath, bool watermark, CancellationToken ct = default) =>
        SendMultipartAsync("/Home/Forum/doReplyThread.html", new Dictionary<string, string>
        {
            ["resto"] = threadId.ToString(), ["name"] = "无名氏", ["title"] = "无标题",
            ["content"] = content, ["water"] = watermark ? "1" : "0"
        }, imagePath, false, ct);

    public string? GetImageUrl(Post post, bool thumbnail = false)
    {
        if (!post.HasImage) return null;
        return $"{CdnBaseUrl}{(thumbnail ? "thumb" : "image")}/{post.ImagePath}{post.ImageExtension}";
    }

    public async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        if (File.Exists(url)) return await File.ReadAllBytesAsync(url, ct);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ApiException("图片地址无效");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _siteClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ApiException($"图片下载失败（{(int)response.StatusCode}）");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct, bool useSite = false, string? baseOverride = null)
    {
        using var request = CreateRequest(HttpMethod.Get, path, useSite, baseOverride);
        using var response = await (useSite ? _siteClient : _client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new ApiException($"服务器返回 {(int)response.StatusCode}：{ExtractError(body)}");
        ThrowIfApiError(body);
        try { return JsonSerializer.Deserialize<T>(body, _json) ?? throw new ApiException("服务器返回了空数据"); }
        catch (JsonException ex) { throw new ApiException($"无法解析服务器数据：{ex.Message}"); }
    }

    private async Task<string> SendTextAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct, bool useSite)
    {
        DiagnosticsLog.Write($"HTTP send method={method} path={path} site={useSite}");
        using var request = CreateRequest(method, path, useSite);
        request.Content = content;
        using var response = await (useSite ? _siteClient : _client).SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        DiagnosticsLog.Write($"HTTP response path={path} status={(int)response.StatusCode} mediaType={response.Content.Headers.ContentType?.MediaType ?? "none"} bodyLength={body.Length}");
        if (!response.IsSuccessStatusCode) throw new ApiException($"提交失败（{(int)response.StatusCode}）：{ExtractError(body)}");
        ThrowIfApiError(body);
        return HtmlText.Clean(body.Trim('"'));
    }

    private async Task<string> SendMultipartAsync(string path, Dictionary<string, string> fields, string? imagePath, bool includeEmptyImage, CancellationToken ct)
    {
        using var multipart = new MultipartFormDataContent();
        foreach (var (key, value) in fields) multipart.Add(new StringContent(value), key);
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            var stream = File.OpenRead(imagePath);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(Path.GetExtension(imagePath)));
            multipart.Add(file, "image", Path.GetFileName(imagePath));
        }
        else if (includeEmptyImage) multipart.Add(new ByteArrayContent([]), "image", "");
        return await SendTextAsync(HttpMethod.Post, path, multipart, ct, useSite: true);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, bool useSite, string? baseOverride = null)
    {
        var request = new HttpRequestMessage(method, (baseOverride ?? (useSite ? BaseUrl : JsonApiBaseUrl)) + path);
        if (!string.IsNullOrWhiteSpace(_settings.Cookie)) request.Headers.TryAddWithoutValidation("Cookie", _settings.Cookie.Trim());
        return request;
    }

    private static void ThrowIfApiError(string body)
    {
        var trimmed = body.Trim();
        if (!trimmed.StartsWith('{'))
        {
            if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            {
                var text = JsonSerializer.Deserialize<string>(trimmed) ?? "";
                if (text.Contains("不存在") || text.Contains("错误") || text.Contains("失败")) throw new ApiException(text);
            }
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new ApiException(root.TryGetProperty("error", out var error) ? error.ToString() : "请求失败");
        }
        catch (JsonException) { }
    }

    private static string ExtractError(string body) => HtmlText.Clean(body).Length > 180 ? HtmlText.Clean(body)[..180] : HtmlText.Clean(body);
    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "image/jpeg"
    };
    public void Dispose()
    {
        _client.Dispose();
        _siteClient.Dispose();
    }
}
