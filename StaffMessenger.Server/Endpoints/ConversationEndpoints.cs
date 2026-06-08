using Microsoft.AspNetCore.Mvc;
using StaffMessenger.Contracts.Conversations;
using StaffMessenger.Server.Data;
using StaffMessenger.Server.Security;

namespace StaffMessenger.Server.Endpoints;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/conversations").WithTags("Conversations");

        group.MapGet("/", async (HttpContext context, [FromServices] MessengerRepository repository, CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var conversations = await repository.GetConversationsAsync(principal.UserId.Value, cancellationToken);
            return Results.Ok(conversations);
        });

        group.MapPost("/direct", async (
            HttpContext context,
            [FromBody] CreateDirectConversationRequest request,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            try
            {
                var conversation = await repository.CreateDirectConversationAsync(
                    principal.UserId.Value,
                    request.PeerHandle,
                    cancellationToken);
                return Results.Ok(conversation);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapGet("/saved", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var conversation = await repository.GetOrCreateSavedConversationAsync(
                principal.UserId.Value,
                cancellationToken);
            return Results.Ok(conversation);
        });

        group.MapGet("/announcements", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var conversation = await repository.GetOrCreateAnnouncementConversationAsync(
                principal.UserId.Value,
                cancellationToken);
            return Results.Ok(conversation);
        });

        group.MapPost("/group", async (
            HttpContext context,
            [FromBody] CreateGroupConversationRequest request,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var conversation = await repository.CreateGroupConversationAsync(
                principal.UserId.Value,
                request,
                cancellationToken);
            return Results.Ok(conversation);
        });

        group.MapPost("/{conversationId:guid}/read", async (
            [FromRoute] Guid conversationId,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
            {
                return Results.Unauthorized();
            }

            await repository.MarkReadAsync(conversationId, principal.UserId.Value, cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{conversationId:guid}", async (
            [FromRoute] Guid conversationId,
            [FromBody] DeleteConversationRequest request,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            try
            {
                await repository.DeleteConversationAsync(
                    conversationId,
                    principal.UserId.Value,
                    request.Scope,
                    cancellationToken);
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        group.MapPost("/announcements/broadcast", async (
            [FromBody] BroadcastAnnouncementRequest request,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var token = configuration["Admin:BroadcastToken"]
                        ?? Environment.GetEnvironmentVariable("STAFFMESSENGER_BROADCAST_TOKEN");
            var provided = context.Request.Headers["X-StaffMessenger-Admin-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token) || !string.Equals(token, provided, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest(new { error = "Announcement text is required." });
            }

            await repository.BroadcastAnnouncementAsync(request.Text.Trim(), cancellationToken);
            return Results.NoContent();
        });

        return routes;
    }
}
