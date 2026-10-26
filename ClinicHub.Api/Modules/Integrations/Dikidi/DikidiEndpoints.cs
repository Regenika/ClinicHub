using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Visits;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Integrations.Dikidi;

/// <summary>
/// Состояние адаптера Dikidi для админки (только Admin). Само сопоставление услуг — в /api/visits/service-map
/// (Source = Dikidi, ExternalKey = название услуги так, как оно здесь показано).
/// </summary>
public static class DikidiEndpoints
{
    public static IEndpointRouteBuilder MapDikidiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations/dikidi").WithTags("Dikidi")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });

        group.MapGet("/status", GetStatus);
        group.MapGet("/unmapped-services", GetUnmapped);
        group.MapPost("/sync", SyncNow);
        return app;
    }

    public record DikidiStatusDto(
        bool Configured, int IntervalMinutes, DateTime? LastAttemptAt, DateTime? LastSuccessAt,
        DateTime? LastErrorAt, string? LastError, DikidiSyncResult? LastResult);

    private static IResult GetStatus(DikidiSyncState state, DikidiOptions options) =>
        Results.Ok(new DikidiStatusDto(
            state.Configured, (int)options.Interval.TotalMinutes, state.LastAttemptAt, state.LastSuccessAt,
            state.LastErrorAt, state.LastError, state.LastResult));

    /// <summary>
    /// Услуги из последней синхронизации, которых нет в сопоставлении. Те, что уже сопоставили после неё,
    /// отфильтрованы; в визиты они попадут при следующей синхронизации.
    /// </summary>
    private static async Task<IResult> GetUnmapped(DikidiSyncState state, ClinicHubDbContext db, CancellationToken ct)
    {
        var list = state.LastResult?.Unmapped ?? [];
        if (list.Count == 0)
            return Results.Ok(list);

        var mapped = (await db.ExternalServiceMaps.AsNoTracking()
                .Where(m => m.Source == VisitSource.Dikidi).Select(m => m.ExternalKey).ToListAsync(ct))
            .Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Results.Ok(list.Where(u => !mapped.Contains(u.Name)).ToList());
    }

    /// <summary>Синхронизировать сейчас (например, сразу после сопоставления услуг).</summary>
    private static async Task<IResult> SyncNow(DikidiSync sync, DikidiOptions options, CancellationToken ct)
    {
        if (!options.IsConfigured)
            return Results.Conflict("Адаптер Dikidi выключен: не заданы Dikidi:Phone, Dikidi:Password, Dikidi:CompanyId.");
        try
        {
            var result = await sync.RunAsync(ct);
            return result is null
                ? Results.Conflict("Синхронизация уже идёт, повторите через минуту.")
                : Results.Ok(result);
        }
        catch (DikidiException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Ошибка Dikidi");
        }
    }
}
