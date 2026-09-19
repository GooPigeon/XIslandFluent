namespace XIslandFluent.Models;

public sealed class AppSettings
{
    public const int MaxCookieSlots = 5;
    public string SubscriptionId { get; set; } = Guid.NewGuid().ToString();
    public string Cookie { get; set; } = "";
    public List<CookieSlot> CookieSlots { get; set; } = [];
    public string ActiveCookieSlotId { get; set; } = "";
    public string Theme { get; set; } = "System";
    public bool WatermarkImages { get; set; }
    public bool CompactCards { get; set; }
    public bool ReferenceVisualEffects { get; set; } = true;

    public void NormalizeCookieSlots()
    {
        CookieSlots ??= [];
        CookieSlots = CookieSlots
            .Where(x => x is not null)
            .GroupBy(x => CookieKey(x.Cookie), StringComparer.Ordinal)
            .Select(x => x.First())
            .Take(MaxCookieSlots)
            .ToList();
        if (!string.IsNullOrWhiteSpace(Cookie) && CookieSlots.All(x => CookieKey(x.Cookie) != CookieKey(Cookie)))
        {
            CookieSlots.Insert(0, CreateCookieSlot(Cookie));
            if (CookieSlots.Count > MaxCookieSlots) CookieSlots.RemoveAt(CookieSlots.Count - 1);
        }
        foreach (var slot in CookieSlots)
        {
            if (string.IsNullOrWhiteSpace(slot.Id)) slot.Id = Guid.NewGuid().ToString();
            slot.Cookie = slot.Cookie.Trim();
            slot.Name = NormalizeCookieDisplayName(slot.Name, slot.Cookie);
        }
        var active = CookieSlots.FirstOrDefault(x => x.Id == ActiveCookieSlotId)
            ?? CookieSlots.FirstOrDefault(x => CookieKey(x.Cookie) == CookieKey(Cookie))
            ?? CookieSlots.FirstOrDefault();
        ActiveCookieSlotId = active?.Id ?? "";
        Cookie = active?.Cookie ?? "";
    }

    public CookieSlot? RememberCookie(string cookie, string? name = null, bool activate = true)
    {
        cookie = cookie.Trim();
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        NormalizeCookieSlots();
        var key = CookieKey(cookie);
        var existing = CookieSlots.FirstOrDefault(x => CookieKey(x.Cookie) == key);
        if (existing is not null)
        {
            existing.Cookie = cookie;
            existing.Name = NormalizeCookieDisplayName(name ?? existing.Name, cookie);
            existing.LastUsedAt = DateTimeOffset.Now;
            if (activate)
            {
                ActiveCookieSlotId = existing.Id;
                Cookie = existing.Cookie;
            }
            return existing;
        }
        if (CookieSlots.Count >= MaxCookieSlots) return null;
        var slot = CreateCookieSlot(cookie, name);
        CookieSlots.Add(slot);
        if (activate)
        {
            ActiveCookieSlotId = slot.Id;
            Cookie = slot.Cookie;
        }
        return slot;
    }

    public static string CookieName(string cookie)
    {
        var value = CookieValue(cookie, "userhash");
        return IsPublicCookieName(value) ? value! : string.IsNullOrWhiteSpace(value) ? "未设置" : "待同步饼干";
    }

    private static string NormalizeCookieDisplayName(string? name, string cookie) =>
        IsPublicCookieName(name) ? name! : CookieName(cookie);

    private static bool IsPublicCookieName(string? value) =>
        value is { Length: 7 } && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static CookieSlot CreateCookieSlot(string cookie, string? name = null) => new()
    {
        Name = NormalizeCookieDisplayName(name, cookie), Cookie = cookie.Trim(), LastUsedAt = DateTimeOffset.Now
    };

    private static string CookieKey(string cookie) => CookieValue(cookie, "userhash") ?? cookie.Trim();

    private static string? CookieValue(string cookie, string name)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..].Trim();
        }
        return null;
    }
}

public sealed class CookieSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "未设置";
    public string Cookie { get; set; } = "";
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;
}
