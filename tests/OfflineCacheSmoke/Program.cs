using XIslandFluent.Models;
using XIslandFluent.Services;

var root = Path.Combine(Path.GetTempPath(), "XIslandFluent-OfflineCacheSmoke-" + Guid.NewGuid().ToString("N"));
try
{
    var cache = new OfflineCacheService(root);
    var post = new Post
    {
        Id = 987654321,
        Title = "离线测试",
        ContentHtml = "<b>缓存内容</b>",
        ImagePath = "fake",
        ImageExtension = ".png",
        ImageUrl = "https://example.invalid/fake.png"
    };
    var packagePath = await cache.SaveAsync(post.Id, post.Title, [post], _ => Task.FromResult(new byte[] { 137, 80, 78, 71 }));
    var listed = cache.List().Single();
    var loaded = await cache.LoadAsync(packagePath);
    if (listed.ThreadId != post.Id || loaded.Posts.Single().CleanContent != "缓存内容") throw new Exception("缓存内容校验失败");
    if (!File.Exists(loaded.Posts.Single().ImageUrl)) throw new Exception("缓存图片未恢复");
    Console.WriteLine($"PASS offline thread={loaded.ThreadId} images={loaded.Images.Count}");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
