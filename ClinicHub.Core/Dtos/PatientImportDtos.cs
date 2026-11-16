namespace ClinicHub.Core.Dtos;

/// <summary>Строка выгрузки клиентов (Dikidi «Клиенты» → Excel). Телефон — как в файле, сервер приведёт к E.164.</summary>
public class PatientImportRowDto
{
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public DateTime? DateOfBirth { get; set; }
    public string? Email { get; set; }
}

public class PatientImportRequestDto
{
    public List<PatientImportRowDto> Rows { get; set; } = new();

    /// <summary>Только посчитать, ничего не записывать.</summary>
    public bool DryRun { get; set; }
}

public class PatientImportResultDto
{
    public int Total { get; set; }
    /// <summary>Новые пациенты.</summary>
    public int Created { get; set; }
    /// <summary>Уже были — дописали пустые поля (дата рождения, почта, имя вместо пустого).</summary>
    public int Updated { get; set; }
    /// <summary>Уже были, дописывать нечего.</summary>
    public int Unchanged { get; set; }
    /// <summary>Телефон не российский мобильный или пустой.</summary>
    public int BadPhone { get; set; }
    /// <summary>Тот же телефон уже встречался выше в файле.</summary>
    public int Duplicates { get; set; }
}
