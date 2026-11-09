# ClinicHub — рассылка SMS для клиники

[![Проверка](https://github.com/Regenika/ClinicHub/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/Regenika/ClinicHub/actions/workflows/ci.yml)
[![Проверка приложений](https://github.com/Regenika/ClinicHub/actions/workflows/ci-apps.yml/badge.svg?branch=master)](https://github.com/Regenika/ClinicHub/actions/workflows/ci-apps.yml)

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

Один раз (PowerShell от имени администратора) откройте порты в брандмауэре (на компьютере разработчика;
на компьютере клиники это делает установщик, см. раздел 7):

```powershell
netsh advfirewall firewall add rule name="ClinicHub API" dir=in action=allow protocol=TCP localport=5112 profile=any
netsh advfirewall firewall add rule name="ClinicHub discovery" dir=in action=allow protocol=UDP localport=45454 profile=any
```

## 2. Запуск программы администратора

Visual Studio: выберите проект `ClinicHub.Admin`, цель Windows Machine, нажмите «Запуск».
Адрес сервера по умолчанию: `http://localhost:5112`.

## 3. Приложение для телефона (ClinicHub SMS)

### 3.1. Установка из релиза (обычный способ)

Готовый подписанный APK лежит в релизах GitHub: **https://github.com/Regenika/ClinicHub/releases/latest**

1. Откройте эту ссылку **в браузере телефона**, скачайте `ClinicHub-SmsAgent-<версия>.apk` и откройте файл.
2. Разрешите браузеру установку приложений (телефон спросит сам) → «Установить».
3. Android 13 и новее: «Настройки» → «Приложения» → «ClinicHub SMS» → ⋮ (три точки) → «Разрешить ограниченные настройки».
   Без этого телефон не даст выдать приложению разрешение на SMS.
4. Откройте приложение → «Подключиться» (раздел 4).

Обновление ставится так же — поверх старой версии, настройки подключения сохраняются.

Если на телефоне стоит старая сборка с именем пакета `com.companyname.clinichub.smsagent` (собранная до #50),
её сначала удалите: новая встанет рядом как отдельное приложение, а старая продолжит работать параллельно.

### 3.2. Как выпустить новую версию

```powershell
git tag smsagent-v0.1.1
git push origin smsagent-v0.1.1
```

Сборка на GitHub (`.github/workflows/smsagent-release.yml`) соберёт APK, подпишет постоянным ключом и создаст релиз
«ClinicHub SMS 0.1.1». Версия в теге — три числа и каждый раз больше прошлой. Номер сборки для Android (versionCode)
берётся из номера запуска и сам растёт, поэтому каждая версия ставится поверх предыдущей.
Вручную (Actions → «Релиз SmsAgent» → Run workflow) — только тестовая сборка в артефактах запуска, без релиза.

### 3.3. Ключ подписи

Все версии подписываются **одним** ключом — иначе обновление не встанет поверх, придётся удалять приложение вместе
с настройками. Ключ создан один раз скриптом `tools/new-smsagent-keystore.ps1`, хранится у Свята и в секретах
репозитория (Settings → Secrets and variables → Actions): `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`,
`ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`. **В git ключ и пароли не кладём никогда.**
Без секретов (форк, PR) сборка проходит с отладочной подписью и ничего не публикует.

### 3.4. Сборка у себя (для разработки)

```powershell
cd ClinicHub.SmsAgent
dotnet publish -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

Файл: `ClinicHub.SmsAgent\bin\Release\net10.0-android\publish\ru.regenika.clinichub.smsagent-Signed.apk`.
Он подписан отладочным ключом этого компьютера: поверх версии из релиза не встанет (и наоборот) — сначала удалите приложение.

## 4. Подключение телефона

1. Телефон и компьютер в одной Wi-Fi сети, сервер запущен.
2. Откройте приложение, нажмите «Подключиться», разрешите SMS и уведомления.
3. Телефон появится на вкладке «Телефоны». Дальше приложение работает в фоне.

На Android 13+ сначала «Разрешить ограниченные настройки» (раздел 3.1, шаг 3), потом выдать разрешение на SMS.

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

## 7. Установка на компьютер клиники

Сервер и сайт ставятся службами Windows («ClinicHub API» и «ClinicHub Web»), база — на SQL Server на другом компьютере
(сейчас у Свята, доступ по VPN или туннелю). Пошаговая инструкция на одной странице и чек-лист проверки — `deploy/README.txt`.

- Сервер: `deploy/api` — `build-package.ps1` собирает `artifacts\ClinicHub-Api-<дата>.zip`, в архиве `install.cmd`.
  Установщик спрашивает подключение к базе и Dikidi, сам создаёт `Jwt:Secret`; секреты хранятся зашифрованными
  (`C:\ClinicHub\Api\config\secrets.dat`, DPAPI службы) и не попадают ни в git, ни в журнал. Порт 5112 и автопоиск
  (UDP 45454) открыты только для локальной сети. Без связи с базой сервер не падает, а повторяет подключение каждые 30 с.
- Сайт: `deploy/web` (раздел 6).
- Автообновление обеих служб с GitHub (демо-компьютеры): `deploy\web\auto-update.ps1 -Register`.
- Резервные копии базы — на компьютере с SQL Server: `deploy\db\backup-setup.ps1 -Register`, восстановление — `deploy/db/RESTORE.txt`.
