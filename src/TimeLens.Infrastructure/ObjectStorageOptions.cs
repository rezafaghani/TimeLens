namespace TimeLens.Infrastructure;

public class ObjectStorageOptions
{
    public string ServiceUrl { get; set; } = "http://localhost:8333";
    public string BucketName { get; set; } = "timelens-ingestion";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
}
