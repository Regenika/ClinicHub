namespace ClinicHub.Core.Entities;

public class Patient
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public bool IsSubscribed { get; set; } = true;
    public string? Notes { get; set; }

    /// <summary>Начало последнего состоявшегося визита (UTC). Пересчитывается модулем Visits.</summary>
    public DateTime? LastVisitAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}