namespace StaffMessenger.Contracts.Attachments;

public enum AttachmentKind
{
    Image,
    Video,
    File,
    Voice
}

public sealed record AttachmentDto(
    Guid Id,
    AttachmentKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256Hex,
    string DownloadUrl,
    int? Width,
    int? Height,
    int? DurationMs,
    DateTimeOffset CreatedAt);

public sealed record AttachmentUploadResponse(
    Guid Id,
    AttachmentKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256Hex,
    string DownloadUrl);
