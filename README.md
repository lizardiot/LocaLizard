# LocaLizard 0.0.9.2

**LocaLizard** — графическая утилита для подготовки установочного ISO Windows IoT Enterprise LTSC с русским языком по умолчанию.

## Скачать

- [Последний релиз LocaLizard](https://github.com/lizardiot/LocaLizard/releases/latest)
- [Все релизы](https://github.com/lizardiot/LocaLizard/releases)
- [LocaLizard.exe для Windows x64 — версия 0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/download/0.0.9.2/LocaLizard.exe)

## Возможности

- GUI для выбора входного ISO, выходного ISO, рабочего каталога, языковых источников и каталога обновлений.
- Поиск стабильных накопительных обновлений (LCU) или указанного KB через Microsoft Update Catalog.
- Фильтрация Preview/Insider/Canary, Windows Server и несовпадающей архитектуры до загрузки; пользователь подтверждает выбранную запись.
- Загрузка с доверенных HTTPS-доменов Microsoft Update, проверка доступного Catalog SHA-1 и вычисление SHA-256. Для каждого подготовленного набора создаются `updates.json`, `catalog-source.json` и `SHA256.txt`.
- Поиск подходящего Client Language Pack ru-RU в совместимом русском ISO, управляемом кэше или через UUP Dump API.
- Настройка русской локали через DISM; установка доступных Language Features из FOD/Language and Optional Features.
- При наличии подходящих ресурсов — обработка Windows Setup `boot.wim` index 2, WinPE index 1 и вложенного WinRE.
- Работа с WIM/ESD и входными split-WIM `.swm`; опциональное разбиение итогового WIM на SWM для FAT32 и режим обслуживания существующего WIM в новый файл.
- Статическая проверка результата, журнал и JSON-аудит.
- Необязательная интерактивная проверка загрузки ISO в Hyper-V: временная VM без сети, VMConnect, визуальное подтверждение экрана Setup и очистка VM.

## Ограничения

- Автопоиск LCU охватывает известные семейства сборок; для старых или нестандартных версий может понадобиться вручную указать KB.
- Перед интеграцией проверьте целевую сборку, архитектуру и состав подготовленной цепочки обновлений. Если Microsoft Update Catalog возвращает неоднозначные данные или меняет структуру страницы, программа должна прекратить автоматическую подготовку, а не угадывать пакет.
- Наличие русского языка внутри донорского WIM не гарантирует наличия самостоятельного Client Language Pack CAB. Совместимость конкретного пакета подтверждается DISM.
- Полнота FOD, речи, OCR, рукописного ввода и локализации WinRE зависит от доступности подходящего репозитория Language and Optional Features.
- Hyper-V тест требует Windows, Hyper-V, прав администратора и ручного подтверждения экрана Setup. Он не запускается в GitHub CI.
- Перед рабочим развёртыванием проверьте созданный ISO на отдельной тестовой машине.


## Запуск

1. Скачайте `LocaLizard.exe` из [релиза 0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.2).
2. Запустите программу и подтвердите запрос прав администратора Windows.
3. Выберите исходный Windows ISO, путь для выходного ISO и рабочую папку.
4. При необходимости задайте русский ISO-источник, FOD/L&O и каталог обновлений.
5. Нажмите **«Проверить условия и создать ISO»**. Для Hyper-V теста также требуется установленный Hyper-V и визуальное подтверждение пользователя.

Python и инструменты разработки устанавливать вручную не требуется. Исходный ISO остаётся без изменений.

## История версий

### [0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.2) — текущий выпуск
Исправлено подключение ISO и диагностика отсутствующего Windows Virtual Disk provider.

### [0.0.9.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.1) — предыдущий выпуск
Исправлена установка ADK после ошибки проверки хеша; актуализированы ссылки на установщики Microsoft.

### [0.0.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9) — предыдущий выпуск
Поиск и подготовка обновлений через Microsoft Update Catalog; релиз содержит только журнал изменений версии 0.0.9. Интерактивная Hyper-V проверка просит подтвердить экран Setup.

### [0.0.8.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.9) — предыдущий выпуск
Повышена устойчивость загрузок и проверок кэша; добавлены цепочки обновлений через явный `updates.json`, обработка Setup/WinPE/WinRE при наличии совместимых ресурсов, поддержка SWM и обслуживание существующего WIM. Автоматический поиск LCU через Microsoft Update Catalog ещё не входил в этот выпуск.

### [0.0.8.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.1) — предыдущий выпуск
Улучшены выбор стабильных источников UUP и проверка архитектуры, повторные загрузки с возобновлением, проверка кэша и аудит локализации. Перед установкой ADK добавлены дополнительные проверки ISO и подписи установщика. WinPE index 1 и WinRE в этом выпуске не изменялись.

### [0.0.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.2) — предыдущий выпуск
Добавлен дополнительный русский ISO-источник для поиска клиентского Language Pack, FOD и компонентов Setup/WinPE. Исходный целевой ISO не заменяется, а применимость найденных пакетов проверяется через DISM.

### [0.0.1.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.9) — предыдущий выпуск
Уточнён поиск базового русского клиентского Language Pack: совпадение SKU в имени CAB больше не обязательно, но проверяются язык и архитектура, а окончательную совместимость определяет DISM. Улучшена диагностика и публикация релизов с сохранением предыдущих тегов.

### [0.0.1.8](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.8) — предыдущий выпуск
Проект и исполняемый файл переименованы в LocaLizard. Добавлены Windows GUI для выбора ISO, проверка структуры результата, журналирование, JSON-отчёт, поиск пакетов ru-RU через UUP Dump API и сборка автономного Windows x64 EXE.

## Документация

- [CHANGELOG.md](CHANGELOG.md) — история разработки в репозитории.
- [Релизы LocaLizard](https://github.com/lizardiot/LocaLizard/releases)
- [Microsoft: добавление языковых пакетов Windows](https://learn.microsoft.com/windows-hardware/manufacture/desktop/add-language-packs-to-windows?view=windows-11)
- [Microsoft: языковые компоненты и FOD](https://learn.microsoft.com/windows-hardware/manufacture/desktop/languages-overview?view=windows-11)
- [Windows IoT Enterprise overview](https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/overview)
- [UUP Dump](https://uupdump.net/)
