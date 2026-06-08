using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using StaffMessenger.Contracts.Messages;
using StaffMessenger.Contracts.Realtime;
using StaffMessenger.Server.Data;
using StaffMessenger.Server.Realtime;
using StaffMessenger.Server.Security;

namespace StaffMessenger.Server.Endpoints;

public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/conversations/{conversationId:guid}/messages").WithTags("Messages");

        group.MapGet("/", async (
            [FromRoute] Guid conversationId,
            [FromQuery] int? limit,
            [FromQuery] DateTimeOffset? before,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            try
            {
                var messages = await repository.GetMessagesAsync(
                    principal.UserId.Value,
                    conversationId,
                    limit ?? 80,
                    before,
                    cancellationToken);
                return Results.Ok(messages);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        group.MapPost("/", async (
            [FromRoute] Guid conversationId,
            [FromBody] SendMessageRequest request,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            [FromServices] IHubContext<MessageHub> hub,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            try
            {
                var message = await repository.SaveUserMessageAsync(
                    principal.UserId.Value,
                    conversationId,
                    request,
                    cancellationToken);

                await hub.Clients
                    .Group(conversationId.ToString("D"))
                    .SendAsync("message.created", new MessageCreatedEvent(message), cancellationToken);

                return Results.Ok(message);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        group.MapDelete("/{messageId:guid}", async (
            [FromRoute] Guid conversationId,
            [FromRoute] Guid messageId,
            [FromBody] DeleteMessageRequest request,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            [FromServices] IHubContext<MessageHub> hub,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            try
            {
                await repository.DeleteMessageAsync(
                    principal.UserId.Value,
                    conversationId,
                    messageId,
                    request.Scope,
                    cancellationToken);

                await hub.Clients
                    .Group(conversationId.ToString("D"))
                    .SendAsync("message.deleted", new
                    {
                        conversationId,
                        messageId,
                        scope = request.Scope.ToString()
                    }, cancellationToken);

                return Results.NoContent();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        return routes;
    }
}
