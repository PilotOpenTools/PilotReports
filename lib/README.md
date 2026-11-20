# lib/pilot - сборки клиента Pilot для компиляции

Типы отчётов (`ReportContext`, `RObject`, `RType`, `QueryBuilder`, `ObjectFields`, `AttributeFields`,
`INUserState`, `RelationType`, `ObjectState`...) не входят в NuGet-пакет `Ascon.Pilot.SDK`.
Они лежат в сборках клиента Pilot, и модуль ссылается на них при компиляции.

**В репозитории этих dll нет**: это проприетарные библиотеки АСКОН, `lib/pilot/*.dll` в `.gitignore`. Нужны шесть файлов:
`Ascon.Pilot.Pilot`, `ClientCore`, `Common`, `DataClasses`, `Core`, `ProjectionsRepository`.

- Ссылки подключены с `Private=false`: в zip модуля сборки не попадают, в рантайме берутся из клиента.
- Сборки Pilot не подписаны строгим именем, поэтому версия клиента не обязана совпадать с версией при компиляции.
  Важно лишь, чтобы используемые типы и члены не изменились.
- Тесты копируют эти сборки в свой каталог вывода: клиента Pilot на раннере нет.

Версия сборок в CI по умолчанию - **26.13.0** (Pilot-BIM 26.13.0.62219), переменная `PILOT_CLIENT_VERSION` в `.gitlab-ci.yml`.

## Откуда берутся сборки

Каталог сборок - свойство MSBuild `PilotClientDir`, его выбирает `PilotClient.props` (общий для модуля и тестов) по порядку:

1. переменная окружения `PILOT_CLIENT_DIR` (или `-p:PilotClientDir=...`) - каталог с шестью dll; если задана, другие места не проверяются;
2. `lib/pilot` проекта, если там есть `Ascon.Pilot.Pilot.dll` (копию кладёт разработчик или CI);
3. установленный клиент `C:\Program Files\ASCON\Pilot-BIM`.

Если в выбранном каталоге нет какой-то из шести dll, сборка падает с ошибкой и перечислением недостающих файлов.

- **Разработчик.** С установленным клиентом Pilot-BIM ничего делать не нужно. Без клиента положите шесть dll в `lib/pilot` или укажите каталог: `$env:PILOT_CLIENT_DIR = "D:\pilot-client"`.
- **Без клиента и без GitLab.** `./tools/get-pilot-client.ps1` скачивает дистрибутив Pilot-BIM из центра загрузок АСКОН (`https://pilot.ascon.ru/release/`), распаковывает MSI клиента без установки (`wix burn extract`, `msiexec /a`) и кладёт шесть dll в `lib/pilot`. Так собирает GitHub Actions (версия в `PILOT_CLIENT_VERSION` workflow, например `26.13.0.62219`). Нужны Windows и .NET SDK.
- **GitLab CI.** Задания `build_module` и `test_module` перед сборкой скачивают архив `pilot-client.zip` из реестра пакетов (Generic Package Registry) этого же проекта GitLab: `<GitLab>/api/v4/projects/<id>/packages/generic/pilot-client/<версия>/pilot-client.zip` с токеном задания. Распаковывают в `lib/pilot`. Нет пакета нужной версии - задание падает с текстом и командой загрузки. Токен задания уходит только в запрос к GitLab: при перенаправлении (например, на хранилище объектов) задание переходит по адресу без токена.
- **Шаблон CI.** Эти блоки есть только в `.gitlab-ci.yml` этого модуля, в общем шаблоне клиентских модулей их нет. Кнопка «Обновить CI» в веб-админке заменит файл шаблоном, и `build_module` упадёт без `lib/pilot`: для этого модуля её не нажимайте, а правки шаблона вносите вручную.

## Загрузка пакета в реестр (администратор)

На машине с установленным клиентом Pilot. Токен GitLab (права `api`, роль не ниже Developer) - в переменной окружения, в вывод он не попадает:

```powershell
$env:GITLAB_TOKEN = "<токен>"
.\tools\publish-pilot-client.ps1 -GitLabUrl http://gitlab.example.local:8080 -ProjectId 9
```

Скрипт собирает шесть dll из каталога клиента (`-ClientDir`, `PILOT_CLIENT_DIR` или `C:\Program Files\ASCON\Pilot-BIM`) в `pilot-client.zip` и загружает его как пакет `pilot-client`. Версия пакета по умолчанию - первые три числа версии `Ascon.Pilot.Pilot.dll` (например, `26.13.0`), её можно задать `-Version`. `-ProjectId` - номер проекта модуля или его путь (`client-modules/module-for-report`).

Если версия пакета не совпадает с `PILOT_CLIENT_VERSION` в `.gitlab-ci.yml`, задайте `PILOT_CLIENT_VERSION` в переменных CI/CD проекта (или поправьте значение по умолчанию).

## Обновление после выхода новой версии клиента

1. Обновите клиент Pilot на машине администратора.
2. Загрузите новый пакет `publish-pilot-client.ps1` (см. выше) и поднимите `PILOT_CLIENT_VERSION`.
3. Проверьте локально, при необходимости - с `PILOT_CLIENT_DIR`:

```powershell
dotnet build -c Release
dotnet test tests/Reports.Tests -c Release
```

Вместе с этим обновите версию пакета `Ascon.Pilot.SDK` в `Reports.csproj` до версии клиента.
