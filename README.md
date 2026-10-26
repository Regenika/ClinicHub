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

Сайту нужен SQL Server (ADR-0009): Express, Developer или LocalDB. База общая с сервером — `ClinicHub`, у сайта своя схема `site`.
Строка подключения в git не хранится, один раз задайте её в user-secrets:

```powershell
cd ClinicHub.Web
dotnet user-secrets set "ConnectionStrings:ClinicHub" "Server=.\SQLEXPRESS;Database=ClinicHub;Trusted_Connection=True;TrustServerCertificate=True"
dotnet run --launch-profile http
```

Без строки подключения сайт открывает страницу `/nastroika-bd`: там вводятся сервер, логин и пароль SQL Server, подключение
проверяется и сохраняется зашифрованным в `ClinicHub.Web/config` (папка в `.gitignore`). Так его настраивают на компьютере клиники.

Сайт откроется на `http://localhost:5242`. При запуске он сам применяет миграции схемы `site`, а пустую базу заполняет из
`site-content.json`, поэтому выглядит так же, как до переноса в БД. Онлайн-запись пока работает в демо-режиме
(выдуманное расписание, заявка никуда не уходит, на странице висит предупреждение).

- Страницы собираются из блоков (ADR-0007): `site.Pages` → `site.PageVersions` → `site.Blocks`, настройки блока — JSON.
  Контакты и ссылки клиники — `site.Settings`. Типы блоков: `ClinicHub.Web/Blocks/BlockTypes.cs`. Пока нет админки (PLAN 4.7–4.8),
  правки — прямо в БД. Чтобы заново наполнить базу из `site-content.json`, удалите строки из `site.Pages`, `site.Settings`,
  `site.CatalogServices`, `site.CatalogDoctors` и перезапустите сайт.
- Услуги с ценами и врачи — **временно** в `site.CatalogServices`, `site.CatalogDoctors`, `site.CatalogDoctorServices`
  (у врача без строк в `CatalogDoctorServices` — все услуги). Сайт перечитывает каталог раз в 30 секунд. По ADR-0008 каталог
  переедет в `ClinicHub.Api` (Issue #13), тогда эти таблицы уйдут. Значения в `[КВАДРАТНЫХ СКОБКАХ]` — заглушки.
- **Веб-админка** — `/admin` (только из локальной сети). Первого редактора создают на `/admin/pervyi-redaktor` с компьютера,
  где стоит сайт; остальных — в разделе «Редакторы». Есть услуги и цены (с историей цен), врачи, настройки сайта и
  редактор страниц: черновик → предпросмотр → публикация, история версий с откатом. Форма полей блока строится по атрибутам
  `[Display]` класса настроек (`Blocks/BlockSettings.cs`), отдельную форму на новый тип блока писать не нужно.
  Формы админки интерактивные (Blazor Server), публичные страницы — без JavaScript.
- Новая миграция схемы сайта: `dotnet ef migrations add <Имя> --project ClinicHub.Web --output-dir Data/Migrations`.
- Источник свободного времени — интерфейс `IBookingSource`. Сейчас `DemoBookingSource`, после выбора МИС будет её адаптер.
- Публичные страницы работают без JavaScript (static SSR), поэтому их нормально индексируют поисковики.

**Установка на компьютер клиники (для показа в локальной сети):**

```powershell
powershell -ExecutionPolicy Bypass -File deploy\web\build-package.ps1
```

Скрипт соберёт `artifacts\ClinicHub-Web-<дата>.zip` (автономная сборка, .NET на компьютер клиники ставить не нужно).
Архив распаковать на компьютере клиники и запустить `install.cmd`. Подробно — в `deploy/web/README.txt`.
