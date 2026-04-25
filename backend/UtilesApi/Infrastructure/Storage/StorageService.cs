using Amazon.S3;
using Amazon.S3.Model;

namespace UtilesApi.Infrastructure.Storage;

public interface IStorageService
{
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType);
    Task<string> GetFileUrl(string fileKey);
    Task DeleteFile(string fileKey);
}

public class LocalStorageService : IStorageService
{
    private readonly string _basePath;
    private readonly string _baseUrl;

    public LocalStorageService(IConfiguration configuration)
    {
        _basePath = configuration["Storage:LocalPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _baseUrl = configuration["Storage:BaseUrl"] ?? "http://localhost:5000/uploads";
        
        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType)
    {
        var fileKey = $"{Guid.NewGuid()}_{Path.GetFileName(fileName)}";
        var filePath = Path.Combine(_basePath, fileKey);
        
        await using var outputStream = File.Create(filePath);
        await fileStream.CopyToAsync(outputStream);
        
        return $"{_baseUrl}/{fileKey}";
    }

    public Task<string> GetFileUrl(string fileKey)
    {
        return Task.FromResult($"{_baseUrl}/{fileKey}");
    }

    public Task DeleteFile(string fileKey)
    {
        var filePath = Path.Combine(_basePath, fileKey);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}

public class S3StorageService : IStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;

    public S3StorageService(IAmazonS3 s3Client, IConfiguration configuration)
    {
        _s3Client = s3Client;
        _bucketName = configuration["AWS:S3:BucketName"] ?? "utiles-platform-images";
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType)
    {
        var fileKey = $"lists/{Guid.NewGuid()}_{Path.GetFileName(fileName)}";
        
        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = fileKey,
            InputStream = fileStream,
            ContentType = contentType
        };

        await _s3Client.PutObjectAsync(request);
        return $"https://{_bucketName}.s3.amazonaws.com/{fileKey}";
    }

    public Task<string> GetFileUrl(string fileKey)
    {
        return Task.FromResult($"https://{_bucketName}.s3.amazonaws.com/{fileKey}");
    }

    public async Task DeleteFile(string fileKey)
    {
        await _s3Client.DeleteObjectAsync(_bucketName, fileKey);
    }
}