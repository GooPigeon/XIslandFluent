using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XIslandFluent.Models;

public sealed class ForumCategory
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sort")] public string Sort { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("forums")] public List<ForumInfo> Forums { get; set; } = [];
}

public sealed class ForumInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("msg")] public string DescriptionHtml { get; set; } = "";
    [JsonPropertyName("fgroup")] public string GroupId { get; set; } = "";
    [JsonPropertyName("sort")] public string Sort { get; set; } = "";
    [JsonPropertyName("showName")] public string DisplayNameHtml { get; set; } = "";
    [JsonPropertyName("interval")] public string Cooldown { get; set; } = "";
    [JsonPropertyName("safe_mode")] public string SafeMode { get; set; } = "";
    [JsonPropertyName("auto_delete")] public string AutoDelete { get; set; } = "";
    [JsonIgnore] public int NumericId => int.TryParse(Id, out var value) ? value : 0;
    [JsonIgnore] public string CleanDescription => HtmlText.Clean(DescriptionHtml);
    public override string ToString() => Name;
}

public sealed class TimelineInfo
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("notice")] public string NoticeHtml { get; set; } = "";
    [JsonPropertyName("max_page")][JsonConverter(typeof(FlexibleIntConverter))] public int MaxPage { get; set; }
    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

public class Post : INotifyPropertyChanged
{
    private const double NormalImageMaxWidth = 720;
    private const double NormalImageMaxHeight = 520;
    private const double CompactImageMaxWidth = 360;
    private const double CompactImageMaxHeight = 220;
    private int _imagePixelWidth;
    private int _imagePixelHeight;
    private bool _isImageCompact;
    private double _imageDisplayWidth = 180;
    private double _imageDisplayHeight = 100;

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int Id { get; set; }
    [JsonPropertyName("fid")][JsonConverter(typeof(FlexibleIntConverter))] public int ForumId { get; set; }
    [JsonPropertyName("ReplyCount")][JsonConverter(typeof(FlexibleIntConverter))] public int ReplyCount { get; set; }
    [JsonPropertyName("reply_count")][JsonConverter(typeof(FlexibleIntConverter))] public int FeedReplyCount { set => ReplyCount = value; }
    [JsonPropertyName("img")] public string ImagePath { get; set; } = "";
    [JsonPropertyName("ext")] public string ImageExtension { get; set; } = "";
    [JsonPropertyName("now")] public string Time { get; set; } = "";
    [JsonPropertyName("user_hash")] public string UserHash { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string ContentHtml { get; set; } = "";
    [JsonPropertyName("sage")][JsonConverter(typeof(FlexibleIntConverter))] public int Sage { get; set; }
    [JsonPropertyName("admin")][JsonConverter(typeof(FlexibleIntConverter))] public int Admin { get; set; }
    [JsonPropertyName("hide")][JsonConverter(typeof(FlexibleIntConverter))] public int Hide { get; set; }
    [JsonPropertyName("Replies")] public List<Post> Replies { get; set; } = [];
    [JsonPropertyName("RemainReplies")][JsonConverter(typeof(FlexibleIntConverter))] public int RemainReplies { get; set; }
    [JsonIgnore] public string CleanContent => HtmlText.Clean(ContentHtml);
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) || Title == "无标题" ? $"No.{Id}" : Title;
    [JsonIgnore] public string Author => Admin != 0 ? $"{(string.IsNullOrWhiteSpace(Name) ? "管理员" : Name)} · {UserHash}" : (string.IsNullOrWhiteSpace(UserHash) ? "无名氏" : UserHash);
    [JsonIgnore] public bool IsAnnouncement => Admin != 0;
    [JsonIgnore] public bool IsPo { get; set; }
    [JsonIgnore] public bool ShowPageDivider { get; set; }
    [JsonIgnore] public string PageDividerText { get; set; } = "";
    [JsonIgnore] public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath) && !string.IsNullOrWhiteSpace(ImageExtension);
    [JsonIgnore] public string? ImageUrl { get; set; }
    [JsonIgnore] public string? ThumbnailUrl { get; set; }
    [JsonIgnore] public double ImageDisplayWidth { get => _imageDisplayWidth; private set => SetField(ref _imageDisplayWidth, value); }
    [JsonIgnore] public double ImageDisplayHeight { get => _imageDisplayHeight; private set => SetField(ref _imageDisplayHeight, value); }
    [JsonIgnore] public bool IsImageCompact { get => _isImageCompact; private set { if (SetField(ref _isImageCompact, value)) OnPropertyChanged(nameof(ImageSizeActionText)); } }
    [JsonIgnore] public bool CanShrinkImage => _imagePixelWidth > CompactImageMaxWidth || _imagePixelHeight > CompactImageMaxHeight;
    [JsonIgnore] public string ImageSizeActionText => IsImageCompact ? "恢复图片" : "缩小图片";
    [JsonIgnore] public string ImageResolutionText => _imagePixelWidth > 0 ? $"原图 {_imagePixelWidth} × {_imagePixelHeight}" : "正在读取图片分辨率";

    public void SetImageResolution(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return;
        _imagePixelWidth = pixelWidth;
        _imagePixelHeight = pixelHeight;
        OnPropertyChanged(nameof(CanShrinkImage));
        OnPropertyChanged(nameof(ImageResolutionText));
        UpdateImageDisplaySize();
    }

    public void ToggleCompactImage()
    {
        if (!CanShrinkImage) return;
        IsImageCompact = !IsImageCompact;
        UpdateImageDisplaySize();
    }

    private void UpdateImageDisplaySize()
    {
        if (_imagePixelWidth <= 0 || _imagePixelHeight <= 0) return;
        var maxWidth = IsImageCompact ? CompactImageMaxWidth : NormalImageMaxWidth;
        var maxHeight = IsImageCompact ? CompactImageMaxHeight : NormalImageMaxHeight;
        var scale = Math.Min(1d, Math.Min(maxWidth / _imagePixelWidth, maxHeight / _imagePixelHeight));
        ImageDisplayWidth = Math.Max(1, Math.Round(_imagePixelWidth * scale));
        ImageDisplayHeight = Math.Max(1, Math.Round(_imagePixelHeight * scale));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class FeedPost : Post
{
    [JsonPropertyName("recent_replies")] public string RecentReplies { get; set; } = "";
}

public sealed class Notice
{
    [JsonPropertyName("content")] public string ContentHtml { get; set; } = "";
    [JsonPropertyName("date")] public long Date { get; set; }
    [JsonPropertyName("enable")] public bool Enabled { get; set; }
    [JsonIgnore] public string CleanContent => HtmlText.Clean(ContentHtml);
}

public sealed class CdnEntry
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("rate")] public double Rate { get; set; }
}

public sealed class OfflineThreadPackage
{
    public int FormatVersion { get; set; } = 1;
    public int ThreadId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.Now;
    public List<Post> Posts { get; set; } = [];
    public Dictionary<int, OfflineImage> Images { get; set; } = [];
}

public sealed class OfflineImage
{
    public string Extension { get; set; } = ".jpg";
    public string Base64 { get; set; } = "";
}

public sealed class OfflineThreadInfo
{
    public int ThreadId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CachedAt { get; set; }
    public string FilePath { get; set; } = "";
    [JsonIgnore] public string DisplayText => $"No.{ThreadId}  {Title}\n缓存于 {CachedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
}

public sealed class ApiException(string message) : Exception(message);

public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number)) return number;
        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out number)) return number;
        if (reader.TokenType == JsonTokenType.Null) return 0;
        using var _ = JsonDocument.ParseValue(ref reader);
        return 0;
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public static partial class HtmlText
{
    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BrRegex();
    [GeneratedRegex("</p>|</div>|</li>", RegexOptions.IgnoreCase)] private static partial Regex BlockRegex();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex("[ \\t]+\\n")] private static partial Regex SpaceBeforeNewlineRegex();
    [GeneratedRegex("\\n{3,}")] private static partial Regex ExtraNewlineRegex();

    public static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = BrRegex().Replace(html, "\n");
        text = BlockRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace("\r", "");
        text = SpaceBeforeNewlineRegex().Replace(text, "\n");
        return ExtraNewlineRegex().Replace(text, "\n\n").Trim();
    }
}
