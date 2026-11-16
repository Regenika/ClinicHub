using ClinicHub.Api.Data;
using ClinicHub.Api.Services;
using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicHub.Api.Tests;

// Данные выдуманные (CLAUDE.md: реальных пациентов в репозитории нет).
public class PatientImportTests
{
    private static ClinicHubDbContext NewDb() => new(new DbContextOptionsBuilder<ClinicHubDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static PatientImportRowDto Row(string name, string phone, DateTime? dob = null) =>
        new() { FullName = name, Phone = phone, DateOfBirth = dob };

    [Fact]
    public async Task CreatesNewSkipsBadAndDuplicates()
    {
        await using var db = NewDb();
        var result = await PatientImport.ApplyAsync(db, new PatientImportRequestDto
        {
            Rows =
            [
                Row("Тестова  Анна", "8 (900) 000-00-01", new DateTime(1990, 5, 3)),
                Row("Тестова Анна (дубль)", "+7 900 000 00 01"),
                Row("Городской", "+7 812 000-00-00"),
                Row("Пусто", ""),
                Row("", "9000000002"),
            ],
        }, CancellationToken.None);

        Assert.Equal(5, result.Total);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(2, result.BadPhone);
        var anna = await db.Patients.SingleAsync(p => p.Phone == "+79000000001");
        Assert.Equal("Тестова Анна", anna.FullName);
        Assert.Equal(PatientSource.Dikidi, anna.Source);
        Assert.Equal("Без имени", (await db.Patients.SingleAsync(p => p.Phone == "+79000000002")).FullName);
    }

    [Fact]
    public async Task ExistingKeepsDataAndFillsBlanks()
    {
        await using var db = NewDb();
        db.Patients.Add(new Patient { FullName = "Иванова Мария", Phone = "+79000000003" });
        db.Patients.Add(new Patient { FullName = "Петров Пётр", Phone = "+79000000004", DateOfBirth = new DateTime(1980, 1, 1) });
        await db.SaveChangesAsync();

        var result = await PatientImport.ApplyAsync(db, new PatientImportRequestDto
        {
            Rows =
            [
                Row("Другое имя", "89000000003", new DateTime(1985, 2, 2)),
                Row("Петров П.", "89000000004", new DateTime(1999, 9, 9)),
            ],
        }, CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Unchanged);
        var maria = await db.Patients.SingleAsync(p => p.Phone == "+79000000003");
        Assert.Equal("Иванова Мария", maria.FullName);
        Assert.Equal(new DateTime(1985, 2, 2), maria.DateOfBirth);
        Assert.Equal(new DateTime(1980, 1, 1), (await db.Patients.SingleAsync(p => p.Phone == "+79000000004")).DateOfBirth);
    }

    [Fact]
    public async Task DryRunWritesNothing()
    {
        await using var db = NewDb();
        var result = await PatientImport.ApplyAsync(db, new PatientImportRequestDto
        {
            DryRun = true,
            Rows = [Row("Тестов Олег", "+79000000005")],
        }, CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, await db.Patients.CountAsync());
    }
}
