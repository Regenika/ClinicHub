using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

/// <summary>Интеграция с сайтом клиники: приём заявок через вебхук.</summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhook")
            .WithTags("Вебхук (сайт клиники)");

        group.MapPost("/requests", Create)
            .AllowAnonymous()
            .AddEndpointFilter<WebhookAuthFilter>();
        group.MapGet("/requests/{id:int}/status", GetStatus).AllowAnonymous();
        group.MapGet("/requests/by-external/{externalId}/status", GetStatusByExternal).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> Create(
        WebhookCreateRequest dto,
        Data.ClinicHubDbContext db,
        CancellationToken ct)
    {
        if (RequestsEndpoints.PurposeError(dto.Request) is { } purposeError)
            return Results.BadRequest(new { Error = purposeError });

        var request = await RequestsEndpoints.CreateRequestCoreAsync(
            dto.Request, db, createdById: null, approveAutomatically: true, ct);

        if (request == null)
            return Results.BadRequest(new { Error = "Невалидная заявка: нужны получатели и текст сообщения." });

        return Results.Created($"/api/webhook/requests/{request.Id}", new
        {
            request.Id,
            request.ExternalId,
            Status = request.Status.ToString(),
        });
    }

    private static async Task<IResult> GetStatus(
        int id,
        Data.ClinicHubDbContext db)
    {
        var data = await db.SmsRequests
            .Where(r => r.Id == id)
            .Select(r => new
            {
                r.Id,
                r.ExternalId,
                Status = r.Status.ToString(),
                Total = r.Recipients.Count,
                Sent = r.Recipients.Count(x => x.Status == RecipientStatus.Sent),
                Failed = r.Recipients.Count(x => x.Status == RecipientStatus.Failed),
            })
            .FirstOrDefaultAsync();

        return data is null ? Results.NotFound() : Results.Ok(data);
    }

    private static async Task<IResult> GetStatusByExternal(
        string externalId,
        Data.ClinicHubDbContext db)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return Results.BadRequest();

        var data = await db.SmsRequests
            .Where(r => r.ExternalId == externalId)
            .Select(r => new
            {
                r.Id,
                r.ExternalId,
                Status = r.Status.ToString(),
                Total = r.Recipients.Count,
                Sent = r.Recipients.Count(x => x.Status == RecipientStatus.Sent),
                Failed = r.Recipients.Count(x => x.Status == RecipientStatus.Failed),
            })
            .FirstOrDefaultAsync();

        return data is null ? Results.NotFound() : Results.Ok(data);
    }
}

/// <summary>Проверка ключа X-Webhook-Api-Key, согласованного с сайтом (AppSetting "WebhookApiKey").</summary>
public class WebhookAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<Data.ClinicHubDbContext>();
        var apiKey = await db.AppSettings
            .Where(s => s.Key == "WebhookApiKey")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        var header = context.HttpContext.Request.Headers["X-Webhook-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.Equals(header, StringComparison.Ordinal))
            return Results.Unauthorized();

        return await next(context);
    }
}