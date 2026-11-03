namespace ClinicHub.Web.Site;

/// <summary>
/// Журнал в файлы logs\site-ГГГГ-ММ-ДД.log. Нужен на компьютере клиники: служба пишет в консоль,
/// которую никто не видит, а по файлу можно разобраться, почему сайт упал. Старые файлы удаляются.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 30;

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    public FileLoggerProvider(string directory, TimeProvider time)
    {
        _directory = directory;
        _time = time;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        // Имя файла и метки — по локальному времени компьютера: так их проще сопоставить с жалобой «упало в 15:40».
        var now = _time.GetLocalNow();
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{ShortLevel(level)}] {category}: {message}";
        if (exception is not null) line += Environment.NewLine + exception;

        lock (_lock)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || _writerDate != today)
                {
                    _writer?.Dispose();
                    var path = Path.Combine(_directory, $"site-{today:yyyy-MM-dd}.log");
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream) { AutoFlush = true };
                    _writerDate = today;
                    DeleteOld(today);
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Журнал не должен ронять сайт: нет места на диске или файл заблокирован — пропускаем запись.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void DeleteOld(DateOnly today)
    {
        var border = today.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "site-*.log"))
        {
            var datePart = Path.GetFileNameWithoutExtension(file)["site-".Length..];
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var date) && date < border)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => level.ToString()
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
