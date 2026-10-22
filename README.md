# ClinicHub — рассылка SMS для клиники

Из чего состоит:

| Проект | Что это |
|---|---|
| `ClinicHub.Api` | Сервер (ASP.NET Core + SQL Server; перед продом — PostgreSQL, ADR-0009). Хранит пациентов и заявки, раздаёт SMS телефонам. |
| `ClinicHub.Admin` | Программа администратора для Windows (заявки, пациенты, телефоны). |
| `ClinicHub.SmsAgent` | Приложение для Android: подключается к серверу и отправляет SMS с SIM телефона. |
| `ClinicHub.Core` | Общие классы (DTO и сущности). |
| `ClinicHub.Web` | Сайт клиники (Blazor, черновик): главная и онлайн-запись. См. раздел 6. |

## 1. Запуск сервера на компьютере

### 1.1. База данных

На этапе разработки сервер работает с SQL Server ([ADR-0009](docs/decisions/0009-sqlserver-for-development.md)): подойдёт LocalDB
(ставится вместе с Visual Studio), SQL Server Express или Developer. Базу создавать руками не нужно: сервер создаст её сам при первом запуске.

### 1.2. Секреты (один раз на компьютер)

Пароли и ключи в git не хранятся. На компьютере разработчика их кладут в `dotnet user-secrets`:

```powershell
cd <папка репозитория>\ClinicHub.Api
dotnet user-secrets set "ConnectionStrings:ClinicHub" "Server=(localdb)\MSSQLLocalDB;Database=ClinicHub;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
dotnet user-secrets set "Jwt:Secret" "<случайная строка не короче 32 символов>"
```

Для SQL Server Express вместо `(localdb)\MSSQLLocalDB` укажите `.\SQLEXPRESS`.

Все возможные настройки перечислены в `ClinicHub.Api/appsettings.example.json`.
На сервере клиники вместо user-secrets задаются переменные окружения: `ConnectionStrings__ClinicHub`, `Jwt__Secret`.
Без строки подключения сервер не запустится, без `Jwt:Secret` — запустится только в режиме разработки (со случайным ключом до перезапуска).

Адаптер журнала Dikidi (только чтение, Issue #15) включается, когда заданы все три значения:

```powershell
dotnet user-secrets set "Dikidi:Phone" "+7..."
dotnet user-secrets set "Dikidi:Password" "..."
dotnet user-secrets set "Dikidi:CompanyId" "<число из адреса журнала ...?company=>"
```

На сервере — `Dikidi__Phone`, `Dikidi__Password`, `Dikidi__CompanyId`. Состояние адаптера видно в `/api/debug/state` и `GET /api/integrations/dikidi/status`.

### 1.3. Запуск

```powershell
cd <папка репозитория>\ClinicHub.Api
dotnet run --launch-profile http
```

Сервер слушает `http://0.0.0.0:5112`. База и таблицы создаются и обновляются сами при запуске (миграции EF).

**Первый вход в Admin.** Пароля по умолчанию нет. В пустой базе сервер создаёт администратора `admin`
со случайным паролем и один раз пишет его в консоль и в журнал (`logs/clinichub.log`), ищите строку «Создан первый администратор».
Свой пароль можно задать заранее: `dotnet user-secrets set "Seed:AdminPassword" "..."` или переменная `SEED_ADMIN_PASSWORD`.

Один раз (PowerShell от имени администратора) откройте порты в брандмауэре:

```powershell
netsh advfirewall firewall add rule name="ClinicHub API" dir=in action=allow protocol=TCP localport=5112 profile=any
netsh advfirewall firewall add rule name="ClinicHub discovery" dir=in action=allow protocol=UDP localport=45454 profile=any
```

## 2. Запуск программы администратора

Visual Studio: выберите проект `ClinicHub.Admin`, цель Windows Machine, нажмите «Запуск».
Адрес сервера по умолчанию: `http://localhost:5112`.

## 3. Сборка APK для телефона

```powershell
cd C:\Users\xcx7276\source\repos\ClinicHub.SmsAgent
dotnet publish -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

Готовый файл (устанавливайте именно `-Signed`):

```
C:\Users\xcx7276\source\repos\ClinicHub.SmsAgent\bin\Release\net10.0-android\publish\com.companyname.clinichub.smsagent-Signed.apk
```

Скопируйте его на телефон и установите (разрешите установку из неизвестных источников).

## 4. Подключение телефона

1. Телефон и компьютер в одной Wi-Fi сети, сервер запущен.
2. Откройте приложение, нажмите «Подключиться», разрешите SMS и уведомления.
3. Телефон появится на вкладке «Телефоны». Дальше приложение работает в фоне.

На Android 13+ для приложения из apk-файла может понадобиться: «О приложении» → три точки → «Разрешить ограниченные настройки», затем выдать разрешение на SMS.

## 5. Журналы и отладка

- Общий журнал сервера и телефонов: `ClinicHub.Api\logs\clinichub.log`.
- С самого компьютера: `http://localhost:5112/api/debug/log` и `http://localhost:5112/api/debug/state`.
- Проверка доступности сервера с телефона (в браузере): `http://<IP компьютера>:5112/api/health` — должно быть `ok`.

## 6. Сайт (`ClinicHub.Web`)

```powershell
cd ClinicHub.Web
dotnet run --launch-profile http
```

Сайт откроется на `http://localhost:5242`. Он пока не связан с сервером: онлайн-запись работает в демо-режиме
(выдуманное расписание, заявка никуда не уходит, на странице висит предупреждение).

- Тексты, услуги, врачи, контакты лежат в `ClinicHub.Web/site-content.json`. Значения в `[КВАДРАТНЫХ СКОБКАХ]` — заглушки.
  Файл перечитывается на лету, перезапуск не нужен.
- Источник свободного времени — интерфейс `IBookingSource`. Сейчас `DemoBookingSource`, после выбора МИС будет её адаптер.
- Публичные страницы работают без JavaScript (static SSR), поэтому их нормально индексируют поисковики.

**Установка на компьютер клиники (для показа в локальной сети):**

```powershell
powershell -ExecutionPolicy Bypass -File deploy\web\build-package.ps1
```

Скрипт соберёт `artifacts\ClinicHub-Web-<дата>.zip` (автономная сборка, .NET на компьютер клиники ставить не нужно).
Архив распаковать на компьютере клиники и запустить `install.cmd`. Подробно — в `deploy/web/README.txt`.
