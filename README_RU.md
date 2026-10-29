# LocaLizard 0.0.9.2

Это руководство к текущему выпуску. Полная история изменений репозитория находится в [CHANGELOG.md](CHANGELOG.md); страница релиза содержит отдельный журнал именно своей версии.

## Скачать и запустить

1. [Скачайте LocaLizard.exe — версия 0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/download/0.0.9.2/LocaLizard.exe).
2. Запустите программу и подтвердите запрос прав администратора Windows.
3. Выберите исходный ISO, выходной ISO и рабочую папку.
4. При необходимости задайте русский ISO-источник, FOD/Language and Optional Features и каталог обновлений.
5. Нажмите «Проверить условия и создать ISO». Для Hyper-V теста также требуется установленный Hyper-V и визуальное подтверждение пользователя.

Python и инструменты разработки устанавливать вручную не нужно. Исходный ISO остаётся без изменений.

## Возможности

- GUI для выбора исходного ISO, выходного ISO, рабочей папки, источников языковых компонентов и каталога обновлений.
- Поиск стабильного накопительного обновления (LCU) или конкретного KB через Microsoft Update Catalog.
- Фильтрация Preview/Insider/Canary, Windows Server и несовпадающей архитектуры до загрузки; пользователь подтверждает выбранную запись.
- Загрузка только с доверенных HTTPS-доменов Microsoft Update, проверка опубликованного Catalog SHA-1 и расчёт SHA-256. Для каждого набора обновлений создаются `updates.json`, `catalog-source.json` и `SHA256.txt`.
- Поиск подходящего Client Language Pack ru-RU в совместимом русском ISO, управляемом кэше или через UUP Dump API.
- Настройка русской локали через DISM и установка доступных Language Features из источника FOD / Language and Optional Features.
- При наличии подходящих компонентов — локализация Windows Setup `boot.wim` index 2, WinPE index 1 и вложенного WinRE.
- Обработка WIM/ESD, входных split-WIM `.swm`, разбиение итогового WIM на SWM для FAT32 и обработка существующего WIM в отдельный выходной файл.
- Статическая проверка образа, журналирование и JSON-аудит.
- Необязательная интерактивная Hyper-V проверка через VMConnect с визуальным подтверждением экрана Windows Setup.

## Ограничения

- Автопоиск LCU охватывает известные семейства сборок; для старых или нестандартных версий может потребоваться вручную указать KB.
- Перед интеграцией проверьте целевую сборку, архитектуру и состав цепочки обновлений. Если Microsoft Update Catalog возвращает неоднозначные результаты или меняет структуру страницы, программа останавливается, а не угадывает пакет.
- Наличие русского языка внутри донорского WIM не гарантирует наличие отдельного Client Language Pack CAB. Окончательную совместимость пакета проверяет DISM.
- Полнота FOD, речи, OCR, рукописного ввода и локализации WinRE зависит от доступности подходящего репозитория Language and Optional Features.
- Подключение ISO зависит от системного Windows Virtual Disk provider. Если Windows сообщает, что поставщик отсутствует (`0xC03A0014`), LocaLizard выводит диагностические рекомендации, но восстановить отсутствующий компонент Windows на уровне ОС не может.
- Hyper-V тест требует Windows, Hyper-V, прав администратора и ручного подтверждения экрана Setup. В GitHub CI он не запускается.
- Перед рабочим развёртыванием проверьте созданный ISO на отдельной тестовой машине.

## История версий

### [0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.2) — текущий выпуск
Исправлено подключение ISO с явным указанием типа ISO; добавлена диагностика отсутствующего Windows Virtual Disk provider и безопасная обработка отказа монтирования.

### [0.0.9.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.1)
Исправлена установка ADK при ошибке проверки хеша `0x80091007`; актуализированы ссылки на официальные установщики Microsoft.

### [0.0.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9)
Добавлен поиск и подготовка накопительных обновлений через Microsoft Update Catalog и интерактивная Hyper-V проверка.

### [0.0.8.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.9)
Добавлены проверка целостности кэша, явные цепочки обновлений через `updates.json`, обработка Setup/WinPE/WinRE при наличии совместимых компонентов, поддержка SWM и обслуживание существующего WIM.

### [0.0.8.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.1)
Улучшены подбор и фильтрация кандидатов UUP по ветке и архитектуре, повторные и возобновляемые загрузки, проверка целостности управляемого кэша, диагностика DISM и предварительная проверка условий обработки ISO.

### [0.0.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.2)
Добавлен дополнительный русский ISO-источник для поиска и копирования отдельных CAB языкового пакета, FOD и компонентов Setup/WinPE. Целевой ISO проверяется отдельно, образ из дополнительного ISO не подменяет целевой IoT-образ.

### [0.0.1.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.9)
Улучшен поиск базового Client Language Pack ru-RU: разрешены распространённые варианты имени CAB без строгого совпадения названия редакции; архитектура и подходящая ветка Windows по-прежнему проверяются.

### [0.0.1.8](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.8) — первый выпуск под именем LocaLizard
Проект переименован в LocaLizard; добавлены Windows GUI, выбор ISO, базовая проверка структуры, журнал и JSON-отчёт, поиск ru-RU пакетов через UUP Dump API и CI-сборка автономного Windows x64 EXE.

## Документация

- [CHANGELOG.md](CHANGELOG.md) — полная история изменений проекта.
- [Релизы LocaLizard](https://github.com/lizardiot/LocaLizard/releases)
- [Microsoft: добавление языковых пакетов Windows](https://learn.microsoft.com/windows-hardware/manufacture/desktop/add-language-packs-to-windows?view=windows-11)
- [Microsoft: языковые компоненты и FOD](https://learn.microsoft.com/windows-hardware/manufacture/desktop/languages-overview?view=windows-11)
- [Windows IoT Enterprise overview](https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/overview)
- [UUP Dump](https://uupdump.net/)
