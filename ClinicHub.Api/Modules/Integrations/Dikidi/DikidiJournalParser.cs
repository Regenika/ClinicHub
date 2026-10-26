using System.Globalization;
using System.Text.Json.Nodes;

namespace ClinicHub.Api.Modules.Integrations.Dikidi;

/// <summary>
/// Разбор ответа <c>POST /ru/owner/ajax/journal/api/</c> c <c>masters.info</c> и <c>masters.records</c>.
/// Формат неофициальный (по обёртке Igor20-1/dikidi-api-client), поэтому разбор терпим к мелочам
/// (число или строка, объект или пустой массив), но если пропали опорные ключи — падает с понятной ошибкой,
/// а не возвращает тихо пустой список: иначе синхронизация решила бы, что все записи удалены.
/// </summary>
public static class DikidiJournalParser
{
    public static DikidiJournal Parse(JsonNode? root)
    {
        if (root is not JsonObject obj)
            throw new DikidiException("Ответ журнала Dikidi не JSON-объект — формат изменился.");

        if (Str(obj["error"]) == "1")
        {
            var message = Str(obj["message"]) ?? "без описания";
            throw new DikidiException($"Dikidi вернул ошибку: {Trim(message, 200)}");
        }

        if ((obj["masters"] ?? obj["master"]) is not JsonObject masters)
            throw new DikidiException("В ответе журнала Dikidi нет ключа «masters» — формат изменился.");
        if (masters["records"] is not JsonObject records)
            throw new DikidiException("В ответе журнала Dikidi нет ключа «masters.records» — формат изменился.");

        var names = new Dictionary<string, string>();
        if (Get(masters["info"], "data") is JsonObject info)
        {
            foreach (var (id, m) in info)
            {
                var name = Str(Get(m, "name")) ?? Str(Get(m, "title"));
                if (!string.IsNullOrWhiteSpace(name))
                    names[id] = name.Trim();
            }
        }

        var list = new List<DikidiRecord>();
        var blocks = 0;

        // records.data: { мастер: { дата: { id записи: {...} } } }. Пустые уровни PHP отдаёт как [].
        foreach (var (masterKey, byDate) in Children(records["data"]))
        {
            foreach (var (dateKey, byId) in Children(byDate))
            {
                foreach (var (recKey, node) in Children(byId))
                {
                    if (node is not JsonObject r || r["begin"] is null)
                        continue;

                    var parsed = ParseRecord(r, masterKey, dateKey, recKey, names);
                    if (parsed is null)
                        blocks++;
                    else
                        list.Add(parsed);
                }
            }
        }

        return new DikidiJournal(list, blocks);
    }

    /// <summary>null — служебная блокировка (перерыв, личное время), а не запись пациента.</summary>
    private static DikidiRecord? ParseRecord(
        JsonObject r, string masterKey, string dateKey, string recKey, Dictionary<string, string> names)
    {
        var info = r["info"] as JsonObject;
        var client = info?["client"] as JsonObject;

        var clientName = First(Str(info?["client_name"]), Str(client?["name"]), Str(info?["username"]), Str(r["client_name"]));
        var clientPhone = First(Str(info?["client_phone"]), Str(client?["phone"]), Str(info?["phone"]), Str(r["client_phone"]));

        var services = new List<string>();
        if ((info?["services"] ?? r["services"]) is JsonArray arr)
        {
            foreach (var s in arr)
            {
                var title = First(Str(Get(s, "title")), Str(Get(s, "name")));
                if (title is not null)
                    services.Add(title);
            }
        }
        if (services.Count == 0 && Str(info?["services_title"]) is string joined)
            services.AddRange(joined.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        var type = Str(r["type"])?.ToLowerInvariant();
        if ((clientName is null && services.Count == 0) || type is "break" or "personal" or "block")
            return null;

        var id = Str(r["id"]) ?? recKey;
        var masterId = Str(r["item_id"]) ?? masterKey;
        var date = ParseDate(Str(r["date"]) ?? dateKey)
            ?? throw new DikidiException($"Запись Dikidi #{id}: не разобрать дату — формат изменился.");
        var start = ParseTime(Str(r["begin"]))
            ?? throw new DikidiException($"Запись Dikidi #{id}: не разобрать время начала — формат изменился.");

        var status = Str(info?["status_id"]) ?? Str(r["status_id"]);
        var state = status is "4" or "5" ? DikidiRecordState.Cancelled : DikidiRecordState.Active;

        return new DikidiRecord(
            id, masterId, names.GetValueOrDefault(masterId) ?? names.GetValueOrDefault(masterKey) ?? "",
            date, start, clientName, clientPhone, services, state);
    }

    /// <summary>Дети объекта как пары «ключ → значение»; у массива ключ — индекс. Прочее — пусто.</summary>
    private static IEnumerable<(string Key, JsonNode? Value)> Children(JsonNode? node) => node switch
    {
        JsonObject o => o.Select(p => (p.Key, p.Value)),
        JsonArray a => a.Select((v, i) => (i.ToString(CultureInfo.InvariantCulture), v)),
        _ => [],
    };

    /// <summary>Поле объекта; у массива или значения полей нет (индексатор JsonNode на них бросил бы исключение).</summary>
    private static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;

    /// <summary>Строка из значения JSON: Dikidi отдаёт одни и те же поля то строкой, то числом.</summary>
    private static string? Str(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        var s = v.TryGetValue<string>(out var text) ? text : v.ToJsonString();
        s = s.Trim();
        return s.Length == 0 || s == "null" ? null : s;
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>«2026-10-20», «20.10.2026» или «2026-10-20 14:30:00».</summary>
    private static DateOnly? ParseDate(string? s)
    {
        if (s is null)
            return null;
        var part = s.Split(' ')[0];
        return DateOnly.TryParseExact(part, ["yyyy-MM-dd", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    /// <summary>«2026-10-20 14:30:00», «14:30:00» или «14:30».</summary>
    private static TimeOnly? ParseTime(string? s)
    {
        if (s is null)
            return null;
        var part = s.Split(' ')[^1];
        return TimeOnly.TryParseExact(part, ["HH:mm:ss", "HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
