# LocaLizard — C# / .NET 8

Ветка C# содержит основной выпуск LocaLizard 0.2.2.8 на Windows Forms / .NET 8. Исторический Python-проект сохранён как исходный reference и recovery path; все опубликованные до 0.2.2.8 версии, теги и файлы релизов оставлены без изменений.

## Сохранение предыдущих выпусков

- Изменения C# выполняются в ветке `rewrite/csharp`.
- Исходные `source/main.py.part01` … `source/main.py.part16`, `test_helpers.py` и прежний Python workflow не заменяются.
- Существующие Git-теги, GitHub Releases и их assets не изменяются и не пересоздаются.
- C# имеет версию 0.2.2.8; Windows build workflow собирает приложение, запускает регрессионные и DISM WIM integration tests.
- Релиз C# публикуется отдельным workflow и не переписывает Python-теги или прежние GitHub Releases.

## Сборка

Требуется .NET 8 SDK и Windows:

```powershell
dotnet build dotnet/LocaLizard.sln -c Release
dotnet run --project dotnet/LocaLizard.SmokeTests/LocaLizard.SmokeTests.csproj -c Release
dotnet publish dotnet/LocaLizard.App/LocaLizard.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Приложение использует штатные Windows-инструменты DISM, PowerShell, Robocopy, Mount-DiskImage и `oscdimg.exe` из Windows ADK Deployment Tools. Установка ADK требует прав администратора, официального установщика Microsoft и проверки его Authenticode-подписи.

## Перенесённая функциональность

- Windows Forms GUI для выбора целевого ISO, русского ISO-донора, выходного ISO, рабочих каталогов, FOD и манифеста обновлений.
- Отдельные drag-and-drop зоны для целевого и донорского ISO; запуск основного окна без повышения прав, запрос UAC только перед DISM/servicing-операцией и восстановление выбранного действия в повышенном процессе.
- Разделитель с изменяемой высотой журнала; сохранение и миграция пользовательских параметров в `%LOCALAPPDATA%\\LocaLizard\\settings.json` с совместимыми именами ключей старой Python-версии.
- Инвентаризация WIM/ESD-индексов через DISM, получение build family/architecture, экспорт ESD в WIM и проверка результата.
- Поиск Client Language Pack ru-RU через UUP Dump, отдельная диагностика пяти LTSC build families, проверка клиентского SKU/языка/архитектуры/семейства сборки, безопасные разрешённые CDN-пути Microsoft, проверка размера и SHA-1, retry/resume для загрузок, извлечение UUP ESD через DISM и установка по `update.mum`.
- Импорт совместимых пакетов из русского ISO-донора, управляемый кэш с package_manifest.json / SHA-256 и проверкой tampering при повторном использовании, установка базового Client Language Pack с повторным выбором только при отказе DISM на `/Add-Package`.
- Офлайн-настройка локали ru-RU и необязательные Language Features Basic, OCR, Text-to-Speech и Handwriting из выбранного FOD/L&O-источника.
- Проверка и применение цепочки обновлений `updates.json`: безопасные имена файлов, целевое семейство сборки и архитектура, SHA-256, отсутствие повторов и упорядочивание checkpoint/LCU/WinRE/SafeOS.
- Поиск LCU по KB или семейству сборки в Microsoft Update Catalog, фильтрация стабильных Windows 10/11 пакетов по KB/build/architecture, разбор DownloadDialog, HTTPS-политика Catalog/CDN, SHA-1 при наличии серверных метаданных, локальный SHA-256 и создание `updates.json`, `catalog-source.json`, `SHA256.txt`.
- Установка официальных Windows ADK Deployment Tools и WinPE Add-on с проверкой подписи Microsoft; поиск и запуск `oscdimg.exe`.
- Локализация Windows Setup (`boot.wim` indexes 2 и 1), применение RU CAB, настройка языка Setup и генерация `lang.ini`; локализация вложенного WinRE при наличии совместимого WinPE_OCs ru-RU ресурса.
- Объединение входного split-WIM `.swm`, необязательное разбиение `install.wim` на части SWM по 3800 MiB, проверка непрерывности и читаемости индексов.
- Режим patch-existing WIM: исходный файл сохраняется; обслуживание идёт на рабочей копии, а новый файл публикуется лишь после повторной проверки числа индексов и записи JSON-аудита.
- Создание ISO, повторное монтирование и статическая проверка `boot.wim` и `install.wim/install*.swm`; JSON-аудит источников, обслуживания, языковых пакетов и проверки.
- Необязательный Hyper-V/VMConnect тест: временная VM Gen 2 без сетевого адаптера, подключение ISO, ожидание запуска и запрос оператору подтвердить экран Windows Setup; временная VM и файлы очищаются.

## Валидация и известные ограничения

CI на Windows выполняет сборку .NET 8 solution, smoke/regression проверки, отдельный DISM WIM integration test и публикацию self-contained win-x64. WIM integration test создаёт синтетический двухиндексный образ через DISM Capture/Append, проверяет Get-WimInfo, монтирует/изменяет/фиксирует первый индекс, повторно применяет оба индекса и проверяет сохранность содержимого. Smoke tests покрывают build/architecture, trust policy URL, Catalog parse/filter/download metadata, UUP Dump sparse responses and no-results, Client LP CAB/ESD/MUM classifier and architecture checks, managed cache manifest + tamper detection, update manifest order/path/hash and SWM sequence.

**Проверенный DISM round-trip на синтетическом WIM не означает, что уже проверена локализация реальных ISO.** Применимость конкретных CAB/MSU к выпускам IoT Enterprise LTSC, установка ADK/WinPE Add-on, загрузка/обслуживание Setup/WinPE/WinRE, сборка реального загрузочного ISO и интерактивный Hyper-V test требуют отдельных прогонов с лицензированными целевыми ISO/WIM и совместимыми источниками. CI не скачивает и не обслуживает пользовательские Windows ISO и не делает визуальное подтверждение VMConnect.

Полный legacy-набор из `test_helpers.py` шире быстрых smoke tests; отдельные проверки Python downloader/cache, Catalog fallback, ADK installer diagnostics, ISO provider errors и UAC resume ещё не все имеют прямой C# unit-test equivalent. Поэтому legacy исходники оставлены в репозитории и могут использоваться для сравнения/восстановления поведения.

## Сценарии, которые требуют ручного подтверждения

- Hyper-V status подтверждает запуск VM, но отображение экрана Setup должен подтвердить оператор в VMConnect.
- Отсутствие совместимых источников RU Client LP/FOD/WinPE CAB не маскируется: соответствующая часть операции отмечается в журнале и аудите.
- Статическая проверка читаемости WIM и структуры ISO не гарантирует успешную установку на всех аппаратных конфигурациях.
