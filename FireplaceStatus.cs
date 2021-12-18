public record FireplaceStatus
{
    public bool IsOn => FlameHeight >= 128;
    public int FlameHeight { get; set; }
}
