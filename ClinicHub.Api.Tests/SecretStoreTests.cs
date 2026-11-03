using System.Text;
using ClinicHub.Api.Data;
using ClinicHub.Api.Services;

namespace ClinicHub.Api.Tests;

/// <summary>Секреты установленного сервера (#48): приём из secrets.json, шифрование, повторное чтение. Значения выдуманные.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clinichub-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly string _config;
    private readonly EventLog _log;

    public SecretStoreTests()
    {
        _config = Path.Combine(_root, "config");
        Directory.CreateDirectory(_config);
        _log = new EventLog(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private void WritePlain(string json) =>
        File.WriteAllText(Path.Combine(_config, SecretStore.PlainFileName), json, new UTF8Encoding(false));

    [Fact]
    public void ОткрытыйФайлШифруетсяИУдаляется()
    {
        WritePlain("""{"ConnectionStrings:ClinicHub":"Server=test;Password=выдуманный","Jwt:Secret":"тестовый-секрет"}""");

        var first = SecretStore.Load(_config, _log);

        Assert.Equal("Server=test;Password=выдуманный", first["ConnectionStrings:ClinicHub"]);
        Assert.False(File.Exists(Path.Combine(_config, SecretStore.PlainFileName)));
        var encrypted = File.ReadAllText(Path.Combine(_config, SecretStore.ProtectedFileName));
        Assert.DoesNotContain("выдуманный", encrypted);
        Assert.DoesNotContain("Server=test", encrypted);

        var second = SecretStore.Load(_config, _log);
        Assert.Equal("тестовый-секрет", second["Jwt:Secret"]);

        // В журнал — только число ключей, не значения.
        Assert.DoesNotContain("выдуманный", File.ReadAllText(_log.FilePath));
    }

    [Fact]
    public void НовыйФайлДополняетИПустоеЗначениеУдаляетКлюч()
    {
        WritePlain("""{"Jwt:Secret":"старый","Dikidi:Phone":"+79990000000"}""");
        SecretStore.Load(_config, _log);

        WritePlain("""{"ConnectionStrings:ClinicHub":"Server=new","Dikidi:Phone":""}""");
        var values = SecretStore.Load(_config, _log);

        Assert.Equal("старый", values["Jwt:Secret"]);
        Assert.Equal("Server=new", values["ConnectionStrings:ClinicHub"]);
        Assert.False(values.ContainsKey("Dikidi:Phone"));
    }

    [Fact]
    public void НетПапки_НетСекретов()
    {
        Assert.Empty(SecretStore.Load(Path.Combine(_root, "нет-такой"), _log));
    }

    [Fact]
    public async Task ГотовностьБазы()
    {
        var readiness = new DatabaseReadiness();
        Assert.False(readiness.IsReady);

        var waiting = readiness.WaitAsync(CancellationToken.None);
        readiness.SetError("сервер базы данных не найден");
        Assert.False(waiting.IsCompleted);
        Assert.Equal("сервер базы данных не найден", readiness.LastError);

        readiness.MarkReady();
        Assert.True(await waiting);
        Assert.Null(readiness.LastError);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(await new DatabaseReadiness().WaitAsync(cts.Token));
    }
}
