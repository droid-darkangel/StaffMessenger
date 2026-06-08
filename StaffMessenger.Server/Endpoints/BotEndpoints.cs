using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using StaffMessenger.Contracts.Bots;
using StaffMessenger.Contracts.Realtime;
using StaffMessenger.Server.Data;
using StaffMessenger.Server.Realtime;
using StaffMessenger.Server.Security;

namespace StaffMessenger.Server.Endpoints;

public static class BotEndpoints
{
    public static IEndpointRouteBuilder MapBotEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/bots").WithTags("Bots");

        group.MapGet("/capabilities", () => Results.Ok(new BotApiCapabilities(
            "v1",
            ["message.created", "message.deleted", "typing", "presence"],
            [
                "GET /api/bots/capabilities",
                "GET /api/bots/conversations",
                "POST /api/bots/conversations/{conversationId}/join",
                "GET /api/bots/conversations/{conversationId}/messages",
                "POST /api/bots/messages"
            ],
            ["Authorization: Bearer <bot-token>"])));

        group.MapGet("/", async (HttpContext context, [FromServices] MessengerRepository repository, CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await repository.GetBotsAsync(principal.UserId.Value, cancellationToken));
        });

        group.MapPost("/", async (
            HttpContext context,
            [FromBody] CreateBotRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] TokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
            {
                return Results.Unauthorized();
            }

            var token = tokenService.CreateBotToken();
            var signingSecret = tokenService.CreateSigningSecret();
            var bot = await repository.CreateBotAsync(
                principal.UserId.Value,
                request,
                token.Token,
                signingSecret,
                token.ExpiresAt,
                cancellationToken);

            return Results.Ok(new CreateBotResponse(
                bot.BotId,
                bot.Name,
                bot.Token,
                bot.SigningSecret,
                bot.ExpiresAt));
        });

        group.MapPost("/messages", async (
            [FromBody] BotMessageRequest request,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            [FromServices] IHubContext<MessageHub> hub,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.BotId is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var envelope = MessengerRepository.CreateBotPlainTextEnvelope(request.Text);
                var message = await repository.SaveBotMessageAsync(
                    principal.BotId.Value,
                    request.ConversationId,
                    request.Text,
                    envelope,
                    request.AttachmentIds,
                    cancellationToken);

                await hub.Clients
                    .Group(request.ConversationId.ToString("D"))
                    .SendAsync("message.created", new MessageCreatedEvent(message), cancellationToken);

                return Results.Ok(message);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        group.MapPost("/conversations/{conversationId:guid}/join", async (
            [FromRoute] Guid conversationId,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.BotId is null)
            {
                return Results.Unauthorized();
            }

            await repository.JoinBotConversationAsync(principal.BotId.Value, conversationId, cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/conversations", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.BotId is null)
            {
                return Results.Unauthorized();
            }

            var conversations = await repository.GetBotConversationsAsync(principal.BotId.Value, cancellationToken);
            return Results.Ok(conversations);
        });

        group.MapGet("/conversations/{conversationId:guid}/messages", async (
            [FromRoute] Guid conversationId,
            [FromQuery] int? limit,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.BotId is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var messages = await repository.GetBotMessagesAsync(
                    principal.BotId.Value,
                    conversationId,
                    limit ?? 80,
                    cancellationToken);
                return Results.Ok(messages);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        return routes;
    }
}
