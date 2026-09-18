using System.IO;
using System.Text.Json;
using XIslandFluent.Models;

namespace XIslandFluent.Services;

public sealed class OfflineCacheService
{
    public const string FileExtension = ".xthread";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _cacheFolder;
    private readonly string _assetFolder;

    public OfflineCacheService(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIslandFluent", "OfflineCache");
        _cacheFolder = Path.Combine(root, "Packages");
        _assetFolder = Path.Combine(root, "Assets");
        Directory.CreateDirectory(_cacheFolder);
        Directory.CreateDirectory(_assetFolder);
    }

    public async Task<string> SaveAsync(int threadId, string title, IEnumerable<Post> posts, Func<string, Task<byte[]>> downloadImage)
    {
        var package = new OfflineThreadPackage
        {
            ThreadId = threadId,
            Title = string.IsNullOrWhiteSpace(title) ? $"No.{threadId}" : title,
            CachedAt = DateTimeOffset.Now,
            Posts = posts.ToList()
        };

        foreach (var post in EnumeratePosts(package.Posts).Where(x => x.HasImage && !string.IsNullOrWhiteSpace(x.ImageUrl)).DistinctBy(x => x.Id))
        {
            try
            {
                var bytes = await downloadImage(post.ImageUrl!);
                package.Images[post.Id] = new OfflineImage
                {
                    Extension = NormalizeExtension(post.ImageExtension),
                    Base64 = Convert.ToBase64String(bytes)
                };
            }
            catch { }
        }

        var path = GetPackagePath(threadId);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(package, JsonOptions));
        return path;
    }

    public async Task<OfflineThreadPackage> LoadAsync(string path)
    {
        var package = JsonSerializer.Deserialize<OfflineThreadPackage>(await File.ReadAllTextAsync(path), JsonOptions)
            ?? throw new InvalidDataException("离线缓存文件无效");
        if (package.FormatVersion != 1 || package.ThreadId <= 0 || package.Posts.Count == 0)
            throw new InvalidDataException("离线缓存格式不受支持");

        var assets = Path.Combine(_assetFolder, package.ThreadId.ToString());
        Directory.CreateDirectory(assets);
        foreach (var post in EnumeratePosts(package.Posts))
        {
            if (!package.Images.TryGetValue(post.Id, out var image) || string.IsNullOrWhiteSpace(image.Base64)) continue;
            var imagePath = Path.Combine(assets, $"{post.Id}{NormalizeExtension(image.Extension)}");
            if (!File.Exists(imagePath)) await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(image.Base64));
            post.ImageUrl = imagePath;
            post.ThumbnailUrl = imagePath;
        }
        return package;
    }

    public async Task<OfflineThreadInfo> ImportAsync(string sourcePath)
    {
        var package = await LoadAsync(sourcePath);
        var destination = GetPackagePath(package.ThreadId);
        File.Copy(sourcePath, destination, true);
        return ToInfo(package, destination);
    }

    public IReadOnlyList<OfflineThreadInfo> List()
    {
        var result = new List<OfflineThreadInfo>();
        foreach (var path in Directory.EnumerateFiles(_cacheFolder, $"*{FileExtension}"))
        {
            try
            {
                var package = JsonSerializer.Deserialize<OfflineThreadPackage>(File.ReadAllText(path), JsonOptions);
                if (package is not null && package.ThreadId > 0) result.Add(ToInfo(package, path));
            }
            catch { }
        }
        return result.OrderByDescending(x => x.CachedAt).ToList();
    }

    public string GetPackagePath(int threadId) => Path.Combine(_cacheFolder, $"{threadId}{FileExtension}");

    private static OfflineThreadInfo ToInfo(OfflineThreadPackage package, string path) => new()
    {
        ThreadId = package.ThreadId,
        Title = package.Title,
        CachedAt = package.CachedAt,
        FilePath = path
    };

    private static IEnumerable<Post> EnumeratePosts(IEnumerable<Post> posts)
    {
        var pending = new Stack<Post>(posts.Reverse());
        var visited = new HashSet<Post>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0)
        {
            var post = pending.Pop();
            if (!visited.Add(post)) continue;
            yield return post;
            foreach (var reply in post.Replies ?? []) pending.Push(reply);
        }
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension;
        if (!value.StartsWith('.')) value = "." + value;
        return value.Length <= 8 && value.All(x => char.IsLetterOrDigit(x) || x == '.') ? value.ToLowerInvariant() : ".jpg";
    }
}
