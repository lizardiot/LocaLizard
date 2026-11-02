# LocaLizard 0.3.6 — C# / .NET 10

Основной выпуск LocaLizard развивается в ветке `main` на Windows Forms / .NET 10. Исторические Python-исходники и уже опубликованные релизы сохранены; ветка `rewrite/csharp` является более старым снимком .NET 8 и не используется для выпуска 0.3.6.

## Сохранение предыдущих выпусков

- Исторические Python-файлы и опубликованные ранее релизы остаются в репозитории; версия 0.3.6 публикуется отдельным тегом и отдельным GitHub Release.
- Централизованная версия C# задаётся в `dotnet/Directory.Build.props`.
- Предыдущие Git-теги и assets выпусков не перезаписываются.
- Workflow выпуска собирает приложение, выполняет regression tests и синтетический DISM WIM integration test, затем публикует Windows x64 assets.
- Hyper-V/VMConnect smoke test остаётся пользовательской опцией и не является условием CI или выпуска.

## Возможности 0.3.6

- Предварительная диагностика ISO/WIM/ESD, build family, архитектуры, донорского ISO, манифеста обновлений и свободного места.
- Поддержка .NET 10 LTS; единственный источник версии — `dotnet/Directory.Build.props`.
- Интеграция INF-драйверов в offline install.wim и применение пользовательского unattend.xml через DISM.
- Полная локализация является режимом по умолчанию; частичная локализация требует явного согласия.
- Временное создание ISO, read-back проверка, SHA-256 и публикация после успешной проверки.
- Журнал `build-state.json` фиксирует завершённые этапы и ошибки для безопасной диагностики. Он не обещает слепое возобновление DISM, если нельзя подтвердить целостность рабочих WIM.

## Сборка

Требуется .NET 10 SDK и Windows:

```powershell
dotnet build dotnet/LocaLizard.sln -c Release
dotnet run --project dotnet/LocaLizard.SmokeTests/LocaLizard.SmokeTests.csproj -c Release
dotnet publish dotnet/LocaLizard.App/LocaLizard.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Значок приложения хранится в `assets/LocaLizard.ico`: MSBuild встраивает его в EXE, а главное окно использует тот же значок при запуске.

Приложение использует штатные Windows-инструменты DISM, PowerShell, Robocopy, Mount-DiskImage и `oscdimg.exe` из Windows ADK Deployment Tools. Установка ADK требует прав администратора, официального установщика Microsoft и проверки его Authenticode-подписи.

## Перенесённая функциональность

- Windows Forms GUI для выбора целевого ISO, русского ISO-донора, выходного ISO, рабочих каталогов, FOD и манифеста обновлений.
- Отдельные drag-and-drop зоны для целевого и донорского ISO; запуск основного окна без повышения прав, запрос UAC только перед DISM/servicing-операцией и восстановление выбранного действия в повышенном процессе.
- Разделитель с изменяемой высотой журнала; сохранение и миграция пользовательских параметров в `%LOCALAPPDATA%\\LocaLizard\\settings.json` с совместимыми именами ключей старой Python-версии.
- Инвентаризация WIM/ESD-индексов через DISM, получение build family/architecture, экспорт ESD в WIM и проверка результата.
- Поиск только русского Client Language Pack `ru-RU` через UUP Dump: без искусственного лимита в 32 кандидата, с проверкой семейства сборки/архитектуры, разрешённых CDN-путей Microsoft, размера и SHA-1, retry/resume и повторным разрешением истёкшей временной ссылки с перепроверкой метаданных; извлечение UUP ESD через DISM и установка по `update.mum`.
- Независимые провайдеры W10MUI и OSBuilder.Public читают списки ссылок и PowerShell CLIXML, отбирают только русские `ru-RU` Client/Server Language Pack, Language Interface Pack и FOD metadata. Фактические файлы скачиваются только с разрешённых Microsoft HTTPS CDN URL и проходят проверку размера, Authenticode и DISM CBS Package Identity.
- Матрица совместимости включает Client build families 10240, 14393, 17763, 19044, 22000, 22621, 22631, 26100 и Server 14393, 17763, 20348, 26100. Для Windows 11 23H2 (22631) явно допускается базовая package family 22621; другие несовпадения build family не угадываются.
- Добавлены отдельные провайдеры локального Microsoft Languages and Optional Features ISO, ADK WinPE и WinRE. Setup/WinPE и вложенный WinRE обслуживаются независимо от install.wim.
- FOD inventory выполняется через DISM `/Get-Capabilities`; проверяются Microsoft signature, ru-RU CBS identity, архитектура и build family. Monolithic language FOD CAB применяются как прямой source; satellite-пакеты требуют DISM `/Export-Source`, после чего capabilities устанавливаются через `/Add-Capability /LimitAccess`. Необязательные FOD не откатывают базовый Language Pack.
- Необязательный Microsoft Store/LXP provider принимает Product ID; RG-Adguard используется только для metadata. APPX/MSIX принимаются лишь с разрешённого Microsoft HTTPS CDN после проверки подписи, Microsoft Publisher, `ru-RU`, архитектуры, bundle manifests и зависимостей. Provisioning через `/Add-ProvisionedAppxPackage` требует подходящий License XML; `/SkipLicense` не используется.
- Для локального managed repository создаётся отдельный `resource_manifest.json`: source ID, пакет, resource type, язык, архитектура, build family, product family, signer, SHA-1 и SHA-256. При повторном использовании проверяются hash и provenance.
- Импорт совместимых пакетов из русского ISO-донора; управляемый кэш schema 2 фиксирует исходный пакет, тип ресурса, язык, архитектуру и семью сборки, проверяет SHA-256 и сохраняет чтение schema 1; установка базового Client Language Pack с повторным выбором только при отказе DISM на `/Add-Package`.
- Офлайн-настройка локали ru-RU и необязательные Language Features Basic, OCR, Text-to-Speech и Handwriting из выбранного FOD/L&O-источника.
- Проверка и применение цепочки обновлений `updates.json`: безопасные имена файлов, целевое семейство сборки и архитектура, SHA-256, отсутствие повторов и упорядочивание checkpoint/LCU/WinRE/SafeOS.
- Поиск LCU по KB или семейству сборки в Microsoft Update Catalog, фильтрация стабильных Windows 10/11 пакетов по KB/build/architecture, разбор DownloadDialog, HTTPS-политика Catalog/CDN, SHA-1 при наличии серверных метаданных, локальный SHA-256 и создание `updates.json`, `catalog-source.json`, `SHA256.txt`.
- Установка официальных Windows ADK Deployment Tools и WinPE Add-on с проверкой подписи Microsoft; поиск и запуск `oscdimg.exe`.
- Локализация Windows Setup (`boot.wim` indexes 2 и 1), применение RU CAB, настройка языка Setup и генерация `lang.ini`; локализация вложенного WinRE при наличии совместимого WinPE_OCs ru-RU ресурса.
- Объединение входного split-WIM `.swm`, необязательное разбиение `install.wim` на части SWM по 3800 MiB, проверка непрерывности и читаемости индексов.
- Режим patch-existing WIM: исходный файл сохраняется; обслуживание идёт на рабочей копии, а новый файл публикуется лишь после повторной проверки числа индексов и записи JSON-аудита.
- Создание ISO, повторное монтирование и статическая проверка `boot.wim` и `install.wim/install*.swm`; JSON-аудит источников, обслуживания, языковых пакетов и проверки.
- Необязательный Hyper-V/VMConnect тест: временная VM Gen 2 без сетевого адаптера, подключение ISO, ожидание запуска и запрос оператору подтвердить экран Windows Setup; временная VM и файлы очищаются. Он не требуется для создания ISO, CI или выпуска.

## Валидация и известные ограничения

CI на Windows выполняет сборку .NET 10 solution, smoke/regression проверки, отдельный DISM WIM integration test и публикацию self-contained win-x64. WIM integration test создаёт синтетический двухиндексный образ через DISM Capture/Append, проверяет Get-WimInfo, монтирует/изменяет/фиксирует первый индекс, повторно применяет оба индекса и проверяет сохранность содержимого. Smoke tests дополнительно проверяют отсутствие ограничения UUP кандидатов первыми 32 записями, повторное разрешение временной ссылки при сохранении метаданных, расширенный кэш, W10MUI/OSBuilder parsing, ru-RU compatibility matrix, FOD inventory, APPX manifests и dependency resolution, ADK/Microsoft ISO providers, а также SHA-256/tamper checks managed repository.

**Проверенный DISM round-trip на синтетическом WIM не означает, что уже проверена локализация реальных ISO.** Применимость конкретных CAB/MSU к выпускам IoT Enterprise LTSC, установка ADK/WinPE Add-on, загрузка/обслуживание Setup/WinPE/WinRE и сборка реального загрузочного ISO рекомендуется дополнительно проверить с лицензированными целевыми ISO/WIM и совместимыми источниками. Hyper-V/VMConnect test — необязательная ручная проверка, не условие сборки или релиза. CI не скачивает и не обслуживает пользовательские Windows ISO и не делает визуальное подтверждение VMConnect.

Полный legacy-набор из `test_helpers.py` шире быстрых smoke tests; отдельные проверки Python downloader/cache, Catalog fallback, ADK installer diagnostics, ISO provider errors и UAC resume ещё не все имеют прямой C# unit-test equivalent. Поэтому legacy исходники оставлены в репозитории и могут использоваться для сравнения/восстановления поведения.

## Сценарии, которые требуют ручного подтверждения

- При включённой Hyper-V проверке отображение экрана Setup подтверждает оператор в VMConnect; этот этап можно пропустить.
- Отсутствие совместимых источников RU Client LP/FOD/WinPE CAB не маскируется: соответствующая часть операции отмечается в журнале и аудите.
- Статическая проверка читаемости WIM и структуры ISO не гарантирует успешную установку на всех аппаратных конфигурациях.
