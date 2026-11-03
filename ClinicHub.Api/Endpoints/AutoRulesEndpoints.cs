using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

/// <summary>CRUD персональных авто-правил (день рождения и подобные точечные триггеры).</summary>
public static class AutoRulesEndpoints
{
    public static IEndpointRouteBuilder MapAutoRulesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auto-rules")
            .WithTags("Авто-рассылки")
            .RequireAuthorization();

        group.MapGet("/", GetList);
        group.MapPost("/", Create);
        group.MapPut("/{id:int}", Update);
        group.MapDelete("/{id:int}", Delete);

        return app;
    }

    private static async Task<IResult> GetList(Data.ClinicHubDbContext db)
    {
        var rules = await db.AutoRules.OrderBy(r => r.Name).ToListAsync();
        return Results.Ok(rules.Select(ToDto));
    }

    private static async Task<IResult> Create(
        AutoRuleInputDto dto,
        Data.ClinicHubDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.TextTemplate))
            return Results.BadRequest("Укажите название и текст сообщения.");
        if (!Enum.IsDefined(dto.Purpose))
            return Results.BadRequest("purpose: Service или Marketing.");

        var rule = new AutoRule
        {
            Name = dto.Name.Trim(),
            Trigger = dto.Trigger,
            Purpose = dto.Purpose,
            TextTemplate = dto.TextTemplate,
            DaysOffset = dto.DaysOffset,
            SendTime = dto.SendTime,
            IsActive = dto.IsActive,
        };

        db.AutoRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/auto-rules/{rule.Id}", ToDto(rule));
    }

    private static async Task<IResult> Update(
        int id,
        AutoRuleInputDto dto,
        Data.ClinicHubDbContext db,
        CancellationToken ct)
    {
        var rule = await db.AutoRules.FindAsync(new object?[] { id }, ct);
        if (rule is null)
            return Results.NotFound();

        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.TextTemplate))
            return Results.BadRequest("Укажите название и текст сообщения.");
        if (!Enum.IsDefined(dto.Purpose))
            return Results.BadRequest("purpose: Service или Marketing.");

        rule.Name = dto.Name.Trim();
        rule.Trigger = dto.Trigger;
        rule.Purpose = dto.Purpose;
        rule.TextTemplate = dto.TextTemplate;
        rule.DaysOffset = dto.DaysOffset;
        rule.SendTime = dto.SendTime;
        rule.IsActive = dto.IsActive;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(rule));
    }

    private static async Task<IResult> Delete(int id, Data.ClinicHubDbContext db, CancellationToken ct)
    {
        var rule = await db.AutoRules.FindAsync(new object?[] { id }, ct);
        if (rule is null)
            return Results.NotFound();

        db.AutoRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static AutoRuleDto ToDto(AutoRule r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Trigger = r.Trigger.ToString(),
        Purpose = r.Purpose.ToString(),
        TextTemplate = r.TextTemplate,
        DaysOffset = r.DaysOffset,
        SendTime = r.SendTime,
        IsActive = r.IsActive,
        LastRunAt = r.LastRunAt,
    };
}
