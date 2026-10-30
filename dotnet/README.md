# LocaLizard — C# / .NET 8

Ветка C# содержит порт Windows-приложения LocaLizard. Исторический Python-проект и опубликованные версии сохранены отдельно; до окончания Windows-интеграционных проверок он остаётся резервной эталонной реализацией.

## Сохранение предыдущих выпусков

- Изменения C# выполняются в ветке `rewrite/csharp`.
- Исходные `source/main.py.part01` … `source/main.py.part16`, `test_helpers.py` и прежний Python workflow не заменяются.
- Существующие Git-теги, GitHub Releases и их assets не изменяются и не пересоздаются.
- Для C# предусмотрена отдельная нумерация и отдельный Windows build workflow.
- C# build workflow собирает, тестирует и загружает артефакт Actions. Публикация GitHub Release должна выполняться отдельным контролируемым release workflow после проверки целевого коммита.

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
- Поиск Client Language Pack ru-RU через UUP Dump, проверка клиентского SKU/языка/архитектуры/семейства сборки, безопасные разрешённые CDN-пути Microsoft, проверка размеров и SHA-1, извлечение UUP ESD через DISM и установка по `update.mum`.
- Импорт совместимых пакетов из русского ISO-донора, кэш языковых пакетов и установка базового Client Language Pack с повторным выбором только при отказе DISM на `/Add-Package`.
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

CI на Windows выполняет сборку .NET 8 solution, smoke/regression проверки, публикацию self-contained win-x64 и загрузку артефакта. Детерминированные проверки покрывают разбор build/architecture, trust policy URL, парсинг и фильтрацию Catalog, метаданные Catalog CDN, settings key migration, SHA-256 манифеста обновлений и порядок split-SWM.

**Это пока не означает, что все сценарии прошли интеграционную проверку на реальных образах.** DISM-монтирование/обслуживание, применимость конкретных CAB/MSU к различным IoT Enterprise LTSC выпускам, реальная установка ADK/WinPE Add-on, сборка загрузочного ISO и интерактивный Hyper-V test требуют отдельного запуска на Windows с тестовыми ISO/WIM. GitHub Actions compile/smoke workflow не выполняет этот полный end-to-end цикл.

Полный legacy-набор из `test_helpers.py` содержит дополнительные проверки downloader/cache, UUP Dump, Catalog fallback, ADK installer diagnostics, ISO provider errors и UAC resume-параметров; эти проверки ещё не все имеют эквивалентный C# unit test. До завершения этих сценариев и проверки ISO в VM не следует удалять или объявлять Python-версию заменённой.

## Сценарии, которые требуют ручного подтверждения

- Hyper-V status подтверждает запуск VM, но отображение экрана Setup должен подтвердить оператор в VMConnect.
- Отсутствие совместимых источников RU Client LP/FOD/WinPE CAB не маскируется: соответствующая часть операции отмечается в журнале и аудите.
- Статическая проверка читаемости WIM и структуры ISO не гарантирует успешную установку на всех аппаратных конфигурациях.
