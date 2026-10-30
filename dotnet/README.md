# LocaLizard — перенос на C#

Здесь размещена новая реализация на .NET 8 / Windows Forms рядом с историческим Python-приложением.

## Сохранение релизов

- Работа ведётся в отдельной ветке rewrite/csharp.
- Исходные файлы source/main.py.part01 … source/main.py.part16 и прежний workflow сборки оставлены без изменений.
- Существующие Git-теги и опубликованные GitHub Releases не изменяются и не пересоздаются.
- C# preview имеет отдельную строку версии. Новый workflow только собирает приложение и загружает CI artifact; он не создаёт, не редактирует и не удаляет релизы или теги.
- Исторические номера версий нельзя переиспользовать для C# сборок.

## Сборка

Требуется .NET 8 SDK на Windows:

    dotnet build dotnet/LocaLizard.sln -c Release
    dotnet run --project dotnet/LocaLizard.SmokeTests/LocaLizard.SmokeTests.csproj -c Release
    dotnet publish dotnet/LocaLizard.App/LocaLizard.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true

Подготовка ISO использует штатные Windows-инструменты DISM, PowerShell, Robocopy и oscdimg из Windows ADK Deployment Tools.

## Уже перенесено в C# preview

- Графический интерфейс с выбором целевого ISO, ISO-донора, выходного файла, папок кэша, FOD и обновлений.
- Копирование исходного ISO в рабочую папку без изменения исходного файла.
- Инвентаризация WIM-индексов через DISM; экспорт ESD в WIM.
- Поиск Client Language Pack ru-RU через UUP Dump, проверка архитектуры/семейства сборки/языка, загрузка только с доверенных Microsoft CDN, проверка размера и SHA-1.
- Извлечение UUP ESD через DISM с установкой через update.mum.
- Добавление базового LP с повторным подбором кандидата только при отказе DISM на Add-Package.
- Настройка ru-RU через корректные параметры DISM; System Preferred UI Language — необязательный шаг.
- Необязательные FOD Basic, OCR, Text-to-Speech и Handwriting только при явном выборе источника.
- Валидация updates.json: схема, семейство сборки, архитектура, безопасные имена файлов, отсутствие дубликатов, SHA-256 и порядок checkpoint/target LCU.
- Сборка загрузочного ISO через oscdimg, повторное подключение и статическая проверка WIM-индексов.
- JSON-аудит локализации.

## Что ещё не достигло паритета с Python

В первом C# preview пока не перенесены локализация Windows Setup/WinPE и WinRE, автоматическая установка ADK, разбиение результата на SWM, режим обработки существующего WIM, интерактивный Hyper-V/VMConnect тест, настройки приложения и полный набор регрессионных тестов. Все эти сценарии остаются в исходной Python-версии. Удалять её можно только после интеграционных проверок C# на Windows с тестовыми ISO.

Smoke tests проверяют разбор сборок и архитектур, политику доверенных URL и контроль SHA-256 для updates.json. Полный цикл DISM/ISO требует Windows и тестовых образов.
