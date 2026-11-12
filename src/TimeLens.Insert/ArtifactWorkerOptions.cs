namespace TimeLens.Insert;

public class ArtifactWorkerOptions
{
    public int MaxRetries { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 5;
}
