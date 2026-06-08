using System.Security.Cryptography;
using StaffMessenger.Contracts.Attachments;

namespace StaffMessenger.Server.Services;

public sealed record StoredUpload(
    AttachmentKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256Hex,
    string StoragePath,
    int? Width,
    int? Height,
    int? DurationMs);

public sealed class FileStorageService
{
    private readonly string _root;

    public FileStorageService(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath = configuration["Storage:UploadsPath"];
        _root = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "uploads")
            : Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(environment.ContentRootPath, configuredPath);
    }

    public async Task<StoredUpload> SaveAsync(
        IFormFile file,
        AttachmentKind kind,
        int? width,
        int? height,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
            throw new InvalidOperationException("Attachment is empty.");

        var now = DateTimeOffset.UtcNow;
        var folder = Path.Combine(_root, now.Year.ToString("0000"), now.Month.ToString("00"));
        Directory.CreateDirectory(folder);

        var safeName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(safeName);
        var storagePath = Path.Combine(folder, $"{Guid.NewGuid():N}{extension}");

        await using var input = file.OpenReadStream();
        await using var output = File.Create(storagePath);
        using var sha = SHA256.Create();

        var buffer = new byte[1024 * 128];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);

        return new StoredUpload(
            kind,
            safeName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.Length,
            Convert.ToHexString(sha.Hash ?? []),
            storagePath,
            width,
            height,
            durationMs);
    }
}
