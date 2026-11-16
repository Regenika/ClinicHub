using System.Globalization;
using System.Text;
using ClinicHub.Core.Dtos;
using ExcelDataReader;

namespace ClinicHub.Web.Admin;

/// <summary>
/// Разбор файла клиентов (Dikidi «Клиенты» → экспорт; подойдёт и любая таблица): xlsx, xls или csv.
/// Колонки ищутся по заголовкам, их можно поправить вручную. Файл никуда не сохраняется — только читается в памяти.
/// </summary>
public sealed class ClientsFile
{
    public const long MaxBytes = 20 * 1024 * 1024;

    public List<string> Headers { get; } = new();
    public List<object?[]> Rows { get; } = new();

    public int? NameCol { get; set; }
    public int? LastNameCol { get; set; }
    public int? FirstNameCol { get; set; }
    public int? MiddleNameCol { get; set; }
    public int? PhoneCol { get; set; }
    public int? BirthCol { get; set; }
    public int? EmailCol { get; set; }

    static ClientsFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ClientsFile Read(Stream stream, string fileName)
    {
        using var reader = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? ExcelReaderFactory.CreateCsvReader(stream, new ExcelReaderConfiguration
            {
                FallbackEncoding = Encoding.GetEncoding(1251),
                AutodetectSeparators = [';', ',', '\t'],
            })
            : ExcelReaderFactory.CreateReader(stream);

        var file = new ClientsFile();
        // Первая строка, где нашлось хоть что-то похожее на заголовок телефона или имени, — заголовок.
        var headerFound = false;
        while (reader.Read())
        {
            var cells = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                cells[i] = reader.GetValue(i);
            if (cells.All(c => c is null || string.IsNullOrWhiteSpace(c.ToString())))
                continue;
            if (!headerFound)
            {
                var texts = cells.Select(c => c?.ToString()?.Trim() ?? "").ToList();
                if (texts.Any(t => Is(t, "телефон", "phone", "мобильн")) || texts.Any(t => Is(t, "фио", "имя", "клиент")))
                {
                    file.Headers.AddRange(texts);
                    headerFound = true;
                }
                continue;
            }
            file.Rows.Add(cells);
        }
        if (!headerFound)
            throw new InvalidDataException("Не нашли строку заголовков: нужна колонка «Телефон» и «Имя» или «ФИО».");
        file.Guess();
        return file;
    }

    private static bool Is(string header, params string[] parts) =>
        parts.Any(p => header.Contains(p, StringComparison.OrdinalIgnoreCase));

    private int? Find(Func<string, bool> match)
    {
        for (var i = 0; i < Headers.Count; i++)
            if (match(Headers[i]))
                return i;
        return null;
    }

    private void Guess()
    {
        PhoneCol = Find(h => Is(h, "телефон", "phone", "мобильн") && !Is(h, "доп", "втор", "2"));
        PhoneCol ??= Find(h => Is(h, "телефон", "phone", "мобильн"));
        BirthCol = Find(h => Is(h, "рожден", "birth", "д.р", "др.") || h.Trim().Equals("ДР", StringComparison.OrdinalIgnoreCase));
        EmailCol = Find(h => Is(h, "mail", "почт"));
        NameCol = Find(h => Is(h, "фио", "full name") || h.Trim().Equals("клиент", StringComparison.OrdinalIgnoreCase));
        LastNameCol = Find(h => Is(h, "фамил", "last name"));
        FirstNameCol = Find(h => h.Trim().Equals("имя", StringComparison.OrdinalIgnoreCase) || Is(h, "first name"));
        MiddleNameCol = Find(h => Is(h, "отчеств", "middle"));
        if (NameCol is null && LastNameCol is null)
            NameCol = FirstNameCol ?? Find(h => Is(h, "имя", "name"));
        if (NameCol is not null && NameCol == FirstNameCol)
            FirstNameCol = null;
    }

    public bool Ready => PhoneCol is not null && (NameCol is not null || LastNameCol is not null || FirstNameCol is not null);

    private string Text(object?[] row, int? col) =>
        col is int i && i < row.Length && row[i] is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "" : "";

    public string NameOf(object?[] row)
    {
        if (NameCol is not null)
            return Text(row, NameCol);
        return string.Join(' ', new[] { Text(row, LastNameCol), Text(row, FirstNameCol), Text(row, MiddleNameCol) }
            .Where(s => s.Length > 0));
    }

    public string PhoneOf(object?[] row)
    {
        var raw = Text(row, PhoneCol);
        // Excel хранит телефон числом: 79991234567 → "79991234567"; double → без «E+10».
        if (PhoneCol is int i && i < row.Length && row[i] is double d)
            raw = d.ToString("0", CultureInfo.InvariantCulture);
        // Несколько номеров в одной ячейке — берём первый.
        return raw.Split([',', ';', '/', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
    }

    public DateTime? BirthOf(object?[] row)
    {
        if (BirthCol is not int i || i >= row.Length || row[i] is null)
            return null;
        switch (row[i])
        {
            case DateTime dt:
                return dt.Date;
            case double serial when serial is > 1 and < 80000:
                return DateTime.FromOADate(serial).Date;
        }
        var s = Text(row, BirthCol);
        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "dd.MM.yyyy H:mm:ss", "dd.MM.yyyy HH:mm", "yyyy-MM-dd HH:mm:ss"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r.Date : null;
    }

    public List<PatientImportRowDto> ToRows() => Rows
        .Select(r => new PatientImportRowDto
        {
            FullName = NameOf(r),
            Phone = PhoneOf(r),
            DateOfBirth = BirthOf(r),
            Email = EmailCol is null ? null : NullIfEmpty(Text(r, EmailCol)),
        })
        .Where(r => r.FullName.Length > 0 || r.Phone.Length > 0)
        .ToList();

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
