namespace ClinicHub.Core.Dtos;

public class PatientDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Вычисляется на сервере от DateOfBirth, для фильтра и отображения.</summary>
    public int? Age { get; set; }
    public bool IsSubscribed { get; set; }
    public string? Notes { get; set; }

    /// <summary>Последний состоявшийся визит (UTC), только для чтения.</summary>
    public DateTime? LastVisitAt { get; set; }
}

public class PatientInputDto
{
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public bool IsSubscribed { get; set; } = true;
    public string? Notes { get; set; }
}
