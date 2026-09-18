using XIslandFluent.Models;
using XIslandFluent.Services;

var settings = new AppSettings();
using var api = new XIslandApiService(settings);
await api.InitializeAsync();
var forums = await api.GetForumsAsync();
var timelines = await api.GetTimelinesAsync();
var notice = await api.GetNoticeAsync();
var feed = await api.GetFeedAsync(Guid.NewGuid().ToString(), 1);
var firstForum = forums.SelectMany(x => x.Forums).FirstOrDefault(x => x.NumericId > 0)
    ?? throw new Exception("API 未返回可用板块");
var posts = await api.GetBoardAsync(firstForum.NumericId, 1);
Console.WriteLine($"PASS CDN={api.CdnBaseUrl} categories={forums.Count} timelines={timelines.Count} notice={notice.Enabled} emptyFeed={feed.Count} board={firstForum.Name} posts={posts.Count}");
if (posts.Count > 0)
{
    var thread = await api.GetThreadAsync(posts[0].Id, 1);
    Console.WriteLine($"PASS thread={thread.Id} replies={thread.Replies.Count}");
}
