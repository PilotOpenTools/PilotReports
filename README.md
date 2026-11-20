<p align="center"><img src="docs/images/banner.png" alt="PilotReports" width="720"></p>

# PilotReports

[![CI](https://github.com/PilotOpenTools/PilotReports/actions/workflows/ci.yml/badge.svg)](https://github.com/PilotOpenTools/PilotReports/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/PilotOpenTools/PilotReports)](https://github.com/PilotOpenTools/PilotReports/releases/latest)

Клиентский модуль Pilot-ICE Enterprise и Pilot-BIM для скриптов отчётов. Отчёт получает общие функции и доступ к Pilot SDK, модуль считает статистику построений и при сохранении отчёта отправляет `.repx` в GitLab, откуда отчёт идёт на тест и прод.

[Руководство](docs/guide.md) · [Скачать релиз](https://github.com/PilotOpenTools/PilotReports/releases/latest) · [История изменений](CHANGELOG.md)

## Что умеет

- **Общие функции для отчётов.** Атрибуты, ФИО, состояния, поиск родителя, оргструктура, поиск объектов, параметры. Набор собран по двум сотням отчётов рабочей базы, чтобы их не копировали из отчёта в отчёт.
- **Pilot SDK из отчёта.** В скрипте отчёта есть только `ReportContext`. Через модуль доступны `IObjectsRepository`, `ISearchService`, `IObjectModifier`, `IFileProvider`, `IPilotDialogService` и другие сервисы.
- **Статистика построений.** Сколько раз строили каждый отчёт и сколько построений прошло успешно. Данные уходят в сервис статистики и в Grafana.
- **CI/CD отчётов.** При сохранении отчёта в дизайнере Pilot модуль предлагает отправить `.repx` в ветку `dev` (тестовый сервер) или в `hotfix/…` от `main`. Репозиторий ищется по имени отчёта. Окно «Загрузить отчёт» сравнивает отчёты на сервере с ветками и загружает новые версии.

```
Скрипт отчёта ──Report.Begin(this, …)──Reports (клиент Pilot) ──после построения──report-metrics-collector
                                              │                                          │
                                  .repx при сохранении                  PostgreSQL metrics.report_events ──Grafana
                                              │
                              GitLab: dev / hotfix/… ──PilotProAdmin──тест / прод
```

Сервис статистики, GitLab и перенос на прод входят в [PilotProAdmin](https://github.com/PilotOpenTools/PilotProAdmin). Общие функции и SDK работают и без них.

## Установка

Файлы релиза на странице [Releases](https://github.com/PilotOpenTools/PilotReports/releases/latest):

| Файл | Что это |
|---|---|
| `112Reports.ext2-<версия>.zip` | модуль для загрузки на сервер Pilot (Pilot-myAdmin или вкладка «Модули» PilotProAdmin) |
| `SHA256SUMS.txt` | контрольные суммы |

1. Загрузите на сервер и назначьте пользователям [`113Pilot.ClientSdk.ext2`](https://github.com/PilotOpenTools/PilotClientSdk/releases/latest), затем `112Reports.ext2`.
2. Впишите в `Reports.config.json` рядом с dll адрес сервиса статистики (`statsUrl`), группу GitLab с отчётами (`reportsGitLabGroupUrl`) и OAuth-приложение GitLab (`gitLabOAuthClientId`). Пока адрес пуст, соответствующая функция выключена. Подробно: [руководство, раздел 5.2](docs/guide.md#52-настройка) и [раздел 6](docs/guide.md#6-отправка-отчётов-в-gitlab-cicd).
3. Если на сервере есть `043SendReportToGitlab.ext2`, снимите его: этот модуль его заменяет.

## Быстрый старт

Скрипт отчёта в дизайнере Pilot:

```csharp
using Reports;

private void PilotReport_DataSourceDemanded(object sender, System.EventArgs e)
{
    Report.Begin(this, "Реестр НОПРИЗ");          // первой строкой: статистика и свежие справочники

    var project = Report.RequireParam<RObject>(this, "Project", "Выберите проект");

    LongRunning.Start(this, () =>
    {
        DataSource = Report.Find("pir_specialistnopriz", inContext: project.Id)
            .Where(o => o.IsAlive() && !o.IsTrainingProject())
            .OrderBy(o => o.Title)
            .Select(o => new { Name = o.Str("name"), State = o.StateTitle(), Link = o.Link() })
            .ToList();
    });
}
```

Без модуля такой код занимал 100-300 строк служебных функций в каждом отчёте. `using Reports;` компилируется, только если модуль назначен пользователю; подключить к старым отчётам одну статистику без этой зависимости можно «мягкой» строкой ([раздел 5.3](docs/guide.md#53-подключение-статистики-к-существующим-отчётам)).

Жизненный цикл построения, справочник API (`Report`, `RObjectExtensions`, `Org`, `Text`, `ReportParams`, `Pilot`), приёмы и типовые проблемы: [docs/guide.md](docs/guide.md).

## Требования

- Клиент Pilot-ICE Enterprise или Pilot-BIM (проверено на 26.13), .NET Framework 4.8, модуль `113Pilot.ClientSdk.ext2` 2.0.0 или новее.
- Для сборки: Windows, .NET SDK 8 или новее (в CI - 10.0).

## Сборка и тесты

Типов отчётов (`ReportContext`, `RObject`, `QueryBuilder`) нет в NuGet-пакете Pilot SDK, они лежат в сборках клиента Pilot. Эти шесть dll проприетарные и в репозиторий не входят. На машине с установленным Pilot-BIM они берутся из `C:\Program Files\ASCON\Pilot-BIM`, без клиента их достаёт из публичного дистрибутива скрипт:

```powershell
./tools/get-pilot-client.ps1          # шесть dll клиента Pilot-BIM 26.13 в lib/pilot
./tools/get-clientsdk.ps1             # пакет Pilot.ClientSdk из релиза и nuget.local.config
dotnet build Reports.sln -c Release -p:RestoreConfigFile=nuget.local.config   # bin\112Reports.ext2-<версия>.zip
dotnet test Reports.sln -c Release --no-build
```

В GitLab стенда пакет Pilot.ClientSdk приходит из реестра пакетов (источник `cicd` в [nuget.config](nuget.config)), а сборки клиента из пакета `pilot-client`: см. [lib/README.md](lib/README.md). Debug-сборка копирует модуль в папку разработки Pilot-BIM, `-p:CopyToPilot=false` отключает копирование.

## Выпуск

1. Поднимите `<Version>` в [Reports.csproj](Reports.csproj) и перенесите записи из «Не выпущено» в [CHANGELOG.md](CHANGELOG.md) под заголовок `## [X.Y.Z] - дата`.
2. Поставьте и отправьте тег:

   ```powershell
   git tag -a vX.Y.Z -m "vX.Y.Z"
   git push origin main vX.Y.Z
   ```

3. Workflow [release.yml](.github/workflows/release.yml) сверит тег с версией, скачает сборки клиента и пакет Pilot.ClientSdk, соберёт и проверит решение и опубликует релиз с zip модуля. Описание берётся из раздела версии в `CHANGELOG.md`.

В GitLab стенда PilotProAdmin модуль ведётся по схеме веток `dev` / `release/*` / `main` с выкладкой на тест, стейдж и прод: этим занимается [.gitlab-ci.yml](.gitlab-ci.yml), подробно в [руководстве](docs/guide.md#7-сборка-отладка-выпуск).

## Участие

Правила участия: [CONTRIBUTING.md](https://github.com/PilotOpenTools/.github/blob/main/CONTRIBUTING.md). Кодекс поведения: [CODE_OF_CONDUCT.md](https://github.com/PilotOpenTools/.github/blob/main/CODE_OF_CONDUCT.md).

## Лицензия

Проект распространяется по лицензии [Apache 2.0](LICENSE). Сборки клиента Pilot принадлежат АСКОН и в проект не входят.
