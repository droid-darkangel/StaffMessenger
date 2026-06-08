using Avalonia.Media.Imaging;

namespace StaffMessenger.Models;

public sealed record AttachmentPreview(
    Guid Id,
    string Kind,
    string FileName,
    string SizeText,
    string Accent,
    string? LocalPath,
    string ContentType,
    Bitmap? PreviewImage = null,
    string? RemoteUrl = null)
{
    public bool HasPreviewImage => PreviewImage is not null;

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public bool IsVideo => ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    public bool IsDocument => !IsImage && !IsVideo;

    public bool ShowVideoPreview => IsVideo && !HasPreviewImage;

    public bool ShowDocumentPreview => IsDocument || (IsImage && !HasPreviewImage);

    public string PreviewTitle => IsVideo ? "Видео" : IsDocument ? "Документ" : "Фото";
}
