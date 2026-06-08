using Microsoft.AspNetCore.Mvc;
using StaffMessenger.Contracts.Attachments;
using StaffMessenger.Server.Data;
using StaffMessenger.Server.Security;
using StaffMessenger.Server.Services;

namespace StaffMessenger.Server.Endpoints;

public static class  AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/attachments").WithTags("Attachments");

        group.MapPost("/", async (
            HttpContext context,
            [FromServices] FileStorageService storage,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
            {
                return Results.Unauthorized();
            }

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var file = form.Files["file"];
            if (file is null)
            {
                return Results.BadRequest(new { error = "Multipart field 'file' is required." });
            }

            var kind = Enum.TryParse<AttachmentKind>(form["kind"], true, out var parsedKind)
                ? parsedKind
                : AttachmentKind.File;

            int? ReadInt(string key) => int.TryParse(form[key], out var value) ? value : null;

            var upload = await storage.SaveAsync(
                file,
                kind,
                ReadInt("width"),
                ReadInt("height"),
                ReadInt("durationMs"),
                cancellationToken);

            var attachment = await repository.SaveAttachmentAsync(principal.UserId.Value, upload, cancellationToken);
            return Results.Ok(new AttachmentUploadResponse(
                attachment.Id,
                attachment.Kind,
                attachment.FileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.Sha256Hex,
                attachment.DownloadUrl));
        });

        group.MapGet("/{attachmentId:guid}/download", async (
            [FromRoute] Guid attachmentId,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (context.GetPrincipal() is null)
            {
                return Results.Unauthorized();
            }

            var attachment = await repository.GetStoredAttachmentAsync(attachmentId, cancellationToken);
            if (attachment is null || !File.Exists(attachment.StoragePath))
            {
                return Results.NotFound();
            }

            return Results.File(
                attachment.StoragePath,
                attachment.ContentType,
                attachment.FileName,
                enableRangeProcessing: true);
        });

        return routes;
    }
}
