namespace XIslandFluent.Models;

public sealed class AppSettings
{
    public string SubscriptionId { get; set; } = Guid.NewGuid().ToString();
    public string Cookie { get; set; } = "";
    public string Theme { get; set; } = "System";
    public bool WatermarkImages { get; set; }
    public bool CompactCards { get; set; }
    public bool ReferenceVisualEffects { get; set; } = true;
}
