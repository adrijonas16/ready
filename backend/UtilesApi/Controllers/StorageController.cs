using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/storage")]
public class StorageController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly INotificationService _notifications;

    public StorageController(IConfiguration config, INotificationService notifications)
    {
        _config = config;
        _notifications = notifications;
    }

    [HttpGet("usage")]
    public async Task<IActionResult> GetUsage()
    {
        var accountId = _config["R2:AccountId"] ?? "";
        var token = _config["R2:ApiToken"] ?? "";
        var bucketName = _config["R2:BucketName"] ?? "ready-utiles";

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        var res = await client.GetAsync($"https://api.cloudflare.com/client/v4/accounts/{accountId}/r2/buckets/{bucketName}/usage");
        var json = await res.Content.ReadAsStringAsync();
        var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

        var result = data.GetProperty("result");
        var payloadBytes = long.Parse(result.GetProperty("payloadSize").GetString() ?? "0");
        var objectCount = int.Parse(result.GetProperty("objectCount").GetString() ?? "0");

        var usedMB = payloadBytes / (1024.0 * 1024.0);
        var freeLimitMB = 10240.0; // 10 GB
        var percentUsed = (usedMB / freeLimitMB) * 100;

        // Alert if > 80% used
        if (percentUsed > 80)
        {
            _ = _notifications.NotifyLowStock($"Cloudflare R2 Storage ({percentUsed:F1}% usado)", 0);
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            objectCount,
            usedBytes = payloadBytes,
            usedMB = Math.Round(usedMB, 2),
            freeLimitMB,
            percentUsed = Math.Round(percentUsed, 2),
            warning = percentUsed > 80 ? "Almacenamiento cercano al limite" : null
        }));
    }

    [HttpDelete("cleanup")]
    public async Task<IActionResult> CleanupTestImages()
    {
        // List and delete test objects from R2
        var accountId = _config["R2:AccountId"] ?? "";
        var accessKey = _config["R2:AccessKeyId"] ?? "";
        var secretKey = _config["R2:SecretAccessKey"] ?? "";
        var bucketName = _config["R2:BucketName"] ?? "ready-utiles";

        var config = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
            ForcePathStyle = true,
        };
        var s3 = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

        var listReq = new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucketName };
        var listRes = await s3.ListObjectsV2Async(listReq);

        var deleted = 0;
        foreach (var obj in listRes.S3Objects)
        {
            // Only delete test files (containing "test" in key)
            if (obj.Key.Contains("test"))
            {
                await s3.DeleteObjectAsync(bucketName, obj.Key);
                deleted++;
            }
        }

        return Ok(ApiResponse<object>.Ok(new { deleted, remaining = listRes.S3Objects.Count - deleted }));
    }
}
