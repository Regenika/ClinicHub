namespace ClinicHub.Web.Data;

/// <summary>
/// Картинка из медиатеки. Хранится В БАЗЕ (решение Свята 2026-10-18, отступление от ADR-0007 §3, где файлы лежат на диске):
/// несколько демо-компьютеров работают с одной базой на ноутбуке, и картинка с диска одного компьютера на других не появилась бы.
/// Сайт отдаёт картинку по /media/{id} с долгим кэшем браузера и держит её в памяти, поэтому в базу ходит один раз на картинку.
/// </summary>
public sealed class MediaFile
{
    public int Id { get; set; }

    /// <summary>Имя файла при загрузке — для списка в медиатеке и для адреса.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Определяется по содержимому файла, а не по тому, что прислал браузер.</summary>
    public string ContentType { get; set; } = "";
    public int Size { get; set; }
    /// <summary>SHA-256 содержимого: ETag и защита от повторной загрузки того же файла.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Описание для незрячих и поисковиков (alt). Подставляется везде, где картинка стоит на сайте.</summary>
    public string Alt { get; set; } = "";

    // Кадрирование: картинка заполняет рамку блока (обложка 5:4, врач 4:5, «до/после» 1:1…), лишнее обрезается.
    // Точка интереса — что должно остаться в кадре при любой рамке; приближение — насколько увеличить.
    /// <summary>Точка интереса по горизонтали, % от левого края (0–100).</summary>
    public int FocusX { get; set; } = 50;
    /// <summary>Точка интереса по вертикали, % от верхнего края (0–100).</summary>
    public int FocusY { get; set; } = 50;
    /// <summary>Приближение, % (100 — без приближения, до 300).</summary>
    public int Zoom { get; set; } = 100;

    public byte[] Data { get; set; } = [];

    public string UploadedBy { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}
