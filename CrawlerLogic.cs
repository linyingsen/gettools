using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using HtmlAgilityPack;
using HapHtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace PromptCrawlerApp;

internal static class CrawlerBootstrap
{
    // 按要求直接写入代码，避免每次运行前配置环境变量。
    public const string ConnectionString = "Server=.;Database=ai;Trusted_Connection=True;TrustServerCertificate=True";

    public static HttpClient BuildHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli
        });

        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json");

        return client;
    }
}

internal sealed class PromptCrawler
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly Action<string> _log;

    public PromptCrawler(HttpClient httpClient, Uri baseUri, Action<string> log)
    {
        _httpClient = httpClient;
        _baseUri = baseUri;
        _log = log;
    }

    public async Task<List<PromptRecord>> CrawlAllAsync()
    {
        var result = new Dictionary<string, PromptRecord>(StringComparer.OrdinalIgnoreCase);

        var visitedListPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingListPages = new Queue<Uri>();
        pendingListPages.Enqueue(new Uri(_baseUri, "/prompt"));

        const int maxListPages = 300;
        var processed = 0;

        while (pendingListPages.Count > 0 && processed < maxListPages)
        {
            var listUri = pendingListPages.Dequeue();
            var listKey = NormalizeUrl(listUri.ToString());
            if (!visitedListPages.Add(listKey))
            {
                continue;
            }

            processed++;
            _log($"抓取列表页：{listUri}");

            var html = await TryGetHtmlAsync(listUri);
            if (string.IsNullOrWhiteSpace(html))
            {
                _log("列表页为空或访问失败，跳过。") ;
                continue;
            }

            var listDoc = new HapHtmlDocument();
            listDoc.LoadHtml(html);

            var detailUrls = ExtractDetailUrls(listDoc, html).ToList();
            _log($"列表页提取到 {detailUrls.Count} 个详情链接。") ;

            foreach (var detailUrl in detailUrls)
            {
                if (result.ContainsKey(detailUrl))
                {
                    continue;
                }

                var prompt = await CrawlDetailAsync(detailUrl, listUri.ToString());
                if (prompt is not null)
                {
                    result[detailUrl] = prompt;
                    _log($"  + {prompt.Title}");
                }
            }

            foreach (var nextListPage in ExtractListPageUrls(listDoc, html))
            {
                var nextKey = NormalizeUrl(nextListPage);
                if (visitedListPages.Contains(nextKey))
                {
                    continue;
                }

                pendingListPages.Enqueue(new Uri(nextListPage));
            }

            await Task.Delay(300);
        }

        _log($"列表页处理完成：共处理 {processed} 页。") ;
        return result.Values.ToList();
    }

    private async Task<PromptRecord?> CrawlDetailAsync(string detailUrl, string sourcePageUrl)
    {
        var html = await TryGetHtmlAsync(new Uri(detailUrl));
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var doc = new HapHtmlDocument();
        doc.LoadHtml(html);

        var title = ReadTitle(doc);
        var summary = ReadSummary(doc);
        var promptText = ReadPromptText(doc);

        // 如果命中的是列表页或内容异常短，则放弃（避免把列表页当成详情页写入）。
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(promptText))
        {
            return null;
        }

        return new PromptRecord
        {
            PromptUrl = detailUrl,
            Title = title,
            Summary = summary,
            PromptText = promptText,
            Tags = ReadTags(doc),
            SourcePageUrl = sourcePageUrl,
            RawHtml = html,
            CrawledAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<string?> TryGetHtmlAsync(Uri uri)
    {
        try
        {
            return await _httpClient.GetStringAsync(uri);
        }
        catch (Exception ex)
        {
            _log($"访问失败：{uri}，错误：{ex.Message}");
            return null;
        }
    }

    private IEnumerable<string> ExtractDetailUrls(HapHtmlDocument doc, string html)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var nodes = doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var node in nodes)
        {
            var href = node.GetAttributeValue("href", string.Empty).Trim();
            var absolute = ToAbsoluteUrl(href);
            if (absolute is null)
            {
                continue;
            }

            if (!IsPromptDetailUrl(absolute))
            {
                continue;
            }

            urls.Add(absolute);
        }

        // 兜底：有些页面列表是前端渲染，详情链接只在 JSON/脚本里。
        foreach (Match m in Regex.Matches(html, "(?:https?:\\/\\/ai\\.codefather\\.cn)?\\/prompt\\/[^\"'\\s<>?#]+", RegexOptions.IgnoreCase))
        {
            var raw = m.Value.Replace("\\/", "/");
            var absolute = ToAbsoluteUrl(raw);
            if (absolute is not null && IsPromptDetailUrl(absolute))
            {
                urls.Add(absolute);
            }
        }

        return urls;
    }

    private IEnumerable<string> ExtractListPageUrls(HapHtmlDocument doc, string html)
    {
        var pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var nodes = doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var node in nodes)
        {
            var href = node.GetAttributeValue("href", string.Empty).Trim();
            var absolute = ToAbsoluteUrl(href);
            if (absolute is null)
            {
                continue;
            }

            if (!IsPromptListUrl(absolute))
            {
                continue;
            }

            pages.Add(NormalizeUrl(absolute));
        }

        // 兜底：页面脚本中提取 /prompt?page=N。
        foreach (Match m in Regex.Matches(html, "(?:https?:\\/\\/ai\\.codefather\\.cn)?\\/prompt\\?[^\"'\\s<>]*page=\\d+[^\"'\\s<>]*", RegexOptions.IgnoreCase))
        {
            var raw = m.Value.Replace("\\/", "/");
            var absolute = ToAbsoluteUrl(raw);
            if (absolute is not null && IsPromptListUrl(absolute))
            {
                pages.Add(NormalizeUrl(absolute));
            }
        }

        return pages;
    }

    private static string ReadTitle(HapHtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode("//h1")
                   ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")
                   ?? doc.DocumentNode.SelectSingleNode("//title");

        var content = node?.Name == "meta"
            ? node.GetAttributeValue("content", string.Empty)
            : node?.InnerText;

        return HtmlEntity.DeEntitize((content ?? string.Empty).Trim());
    }

    private static string ReadSummary(HapHtmlDocument doc)
    {
        var metaDesc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "");
        if (!string.IsNullOrWhiteSpace(metaDesc))
        {
            return metaDesc.Trim();
        }

        var p = doc.DocumentNode.SelectSingleNode("(//article//p)[1]")
                ?? doc.DocumentNode.SelectSingleNode("(//main//p)[1]")
                ?? doc.DocumentNode.SelectSingleNode("(//p)[1]");
        return HtmlEntity.DeEntitize(p?.InnerText?.Trim() ?? string.Empty);
    }

    private static string ReadPromptText(HapHtmlDocument doc)
    {
        var codeNode = doc.DocumentNode.SelectSingleNode("//pre")
                      ?? doc.DocumentNode.SelectSingleNode("//code")
                      ?? doc.DocumentNode.SelectSingleNode("//textarea")
                      ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'prompt') and not(self::script)]")
                      ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'markdown') and not(self::script)]")
                      ?? doc.DocumentNode.SelectSingleNode("//article")
                      ?? doc.DocumentNode.SelectSingleNode("//main");

        if (codeNode is not null)
        {
            var text = HtmlEntity.DeEntitize(codeNode.InnerText).Trim();
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 20)
            {
                return text;
            }
        }

        var scriptNode = doc.DocumentNode.SelectSingleNode("//script[contains(text(), '__NEXT_DATA__')]");
        if (scriptNode is null)
        {
            return string.Empty;
        }

        var extracted = ExtractPossiblePromptFromJson(scriptNode.InnerText);
        return string.IsNullOrWhiteSpace(extracted) ? string.Empty : extracted;
    }

    private static string ReadTags(HapHtmlDocument doc)
    {
        var tags = doc.DocumentNode
            .SelectNodes("//a[contains(@href,'tag') or contains(@href,'category')] | //span[contains(@class,'tag')]")
            ?.Select(n => HtmlEntity.DeEntitize(n.InnerText.Trim()))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        return tags.Count == 0 ? string.Empty : string.Join(',', tags);
    }

    private static string ExtractPossiblePromptFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var candidates = new List<string>();
            Traverse(doc.RootElement, candidates);

            return candidates.Where(x => x.Length > 50)
                .OrderByDescending(x => x.Length)
                .FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Traverse(JsonElement element, List<string> collector)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var name = prop.Name.ToLowerInvariant();
                        if (name.Contains("prompt") || name.Contains("content") || name.Contains("text") || name.Contains("description"))
                        {
                            collector.Add(prop.Value.GetString() ?? string.Empty);
                        }
                    }

                    Traverse(prop.Value, collector);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Traverse(item, collector);
                }

                break;
        }
    }

    private bool IsPromptListUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Equals(_baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Equals("/prompt", StringComparison.OrdinalIgnoreCase) && uri.Query.Contains("page=", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPromptDetailUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Equals(_baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.StartsWith("/prompt/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 排除分页链接、搜索等非详情。
        return !uri.Query.Contains("page=", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private string? ToAbsoluteUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.StartsWith("javascript", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalizedHref = href.Replace("\\/", "/");

        if (Uri.TryCreate(normalizedHref, UriKind.Absolute, out var absolute))
        {
            return absolute.Host.Equals(_baseUri.Host, StringComparison.OrdinalIgnoreCase)
                ? NormalizeUrl(absolute.ToString())
                : null;
        }

        if (Uri.TryCreate(_baseUri, normalizedHref, out var relative))
        {
            return NormalizeUrl(relative.ToString());
        }

        return null;
    }
}

internal static class PromptRepository
{
    public static async Task EnsureTableAsync(IDbConnection connection)
    {
        const string sql = """
IF OBJECT_ID(N'dbo.prompt', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.prompt
    (
        id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        prompt_url      NVARCHAR(500) NOT NULL,
        title           NVARCHAR(300) NOT NULL,
        summary         NVARCHAR(MAX) NULL,
        prompt_text     NVARCHAR(MAX) NULL,
        tags            NVARCHAR(500) NULL,
        source_page_url NVARCHAR(500) NULL,
        raw_html        NVARCHAR(MAX) NULL,
        crawled_at      DATETIMEOFFSET(0) NOT NULL,
        updated_at      DATETIMEOFFSET(0) NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE UNIQUE INDEX UX_prompt_prompt_url ON dbo.prompt(prompt_url);
END;
""";

        await connection.ExecuteAsync(sql);
    }

    public static async Task<int> UpsertAsync(IDbConnection connection, IReadOnlyCollection<PromptRecord> records)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        const string sql = """
MERGE dbo.prompt AS target
USING (VALUES (@PromptUrl, @Title, @Summary, @PromptText, @Tags, @SourcePageUrl, @RawHtml, @CrawledAt))
      AS source (prompt_url, title, summary, prompt_text, tags, source_page_url, raw_html, crawled_at)
ON target.prompt_url = source.prompt_url
WHEN MATCHED THEN
    UPDATE SET
        title = source.title,
        summary = source.summary,
        prompt_text = source.prompt_text,
        tags = source.tags,
        source_page_url = source.source_page_url,
        raw_html = source.raw_html,
        crawled_at = source.crawled_at,
        updated_at = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (prompt_url, title, summary, prompt_text, tags, source_page_url, raw_html, crawled_at)
    VALUES (source.prompt_url, source.title, source.summary, source.prompt_text, source.tags, source.source_page_url, source.raw_html, source.crawled_at);
""";

        var affected = 0;
        foreach (var record in records)
        {
            affected += await connection.ExecuteAsync(sql, record);
        }

        return affected;
    }
}

internal sealed class PromptRecord
{
    public string PromptUrl { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string PromptText { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public string SourcePageUrl { get; init; } = string.Empty;
    public string RawHtml { get; init; } = string.Empty;
    public DateTimeOffset CrawledAt { get; init; }
}
