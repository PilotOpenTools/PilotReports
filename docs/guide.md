# Reports - модуль для отчётов Pilot

# Общие данные

- Постановщик задания на создание: -
- Отделы, использующие модуль: все, кто строит отчёты в Pilot
- Задача в Битриксе и/или в Инфраменеджере: -
- Зависит от `113Pilot.ClientSdk.ext2` 2.0.0 или новее: он должен быть назначен пользователю (окно загрузки модулей, общие настройки статистики). **`043SendReportToGitlab.ext2` заменён этим модулем - снимите его с сервера**, иначе при сохранении отчёта появятся два окна.
- Веб-админка: `proadmin`, вкладка «Отчёты» (перенос на прод, hotfix, откат, создание репозиториев)
- Токен GitLab пользователя: `%AppData%\ASCON\Reports\gitlab\<хеш логина Pilot и сервера>.dat` (свой у каждого пользователя, зашифрован DPAPI)
- Имя общих настроек: **«PilotOpenTools отправка статистики»** из Pilot.ClientSdk (только статистика), остальное в `Reports.config.json` рядом с dll
- Путь к файлу логирования: `%AppData%\ASCON\Units Logs\Logs\Reports.log`

# Краткое описание

Клиентский модуль Pilot (`112Reports.ext2`), которым пользуются скрипты отчётов. Он делает четыре вещи.

1. **Общие функции для отчётов.** Функции, которые раньше копировались из отчёта в отчёт: атрибуты, ФИО, состояния, поиск родителя, оргструктура, поиск объектов, параметры. Список собран по 204 отчётам базы (см. [раздел 4](#4-справочник-api-модуля)).
2. **Доступ к Pilot SDK из отчёта.** В скрипте отчёта есть только `ReportContext`. Через модуль доступны `IObjectsRepository`, `ISearchService`, `IObjectModifier`, `IFileProvider`, `IPilotDialogService` и другие сервисы SDK.
3. **Статистика построений.** Модуль считает, сколько раз строили каждый отчёт и сколько построений прошло успешно. Данные уходят в сервис [`report-metrics-collector`](https://github.com/PilotOpenTools/PilotProAdmin/blob/main/stack/metrics/report-collector/README.md), оттуда в PostgreSQL и в Grafana (дашборд «Pilot - отчёты»).
4. **CI/CD отчётов.** При сохранении отчёта в Pilot модуль предлагает отправить `.repx` в ветку `dev` (тестовый сервер) или, для исправления прода, в ветку `hotfix/…` от `main` репозитория отчёта в GitLab. Репозиторий ищется **по имени отчёта**, ссылка `GitLabLink` в скрипте больше не нужна. На прод отчёт переносится из веб-админки (см. [раздел 6](#6-отправка-отчётов-в-gitlab-cicd)).

```
Скрипт отчёта ──Report.Begin(this,…)──Reports (клиент Pilot)
                                               │  отправка сразу после построения

                               report-metrics-collector (Docker, :5310)
                                               │

                         PostgreSQL pilot_metrics.metrics.report_events ──Grafana
```

---

## Содержание

1. [Быстрый старт для автора отчёта](#1-быстрый-старт-для-автора-отчёта)
2. [Как устроены отчёты в Pilot](#2-как-устроены-отчёты-в-pilot)
3. [Приёмы и подводные камни](#3-приёмы-и-подводные-камни)
4. [Справочник API модуля](#4-справочник-api-модуля)
5. [Статистика построений](#5-статистика-построений)
6. [Отправка отчётов в GitLab (CI/CD)](#6-отправка-отчётов-в-gitlab-cicd)
7. [Сборка, отладка, выпуск](#7-сборка-отладка-выпуск)
8. [Как это устроено внутри](#8-как-это-устроено-внутри)
9. [Типовые проблемы](#9-типовые-проблемы)

---

## 1. Быстрый старт для автора отчёта

Минимальный скрипт отчёта с модулем:

```csharp
using Ascon.Pilot.Pilot.Reports;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.ClientCore.Search;
using Reports;                                   // / модуль
using System.Linq;
using System.Collections.Generic;

private void PilotReport_DataSourceDemanded(object sender, System.EventArgs e)
{
    Report.Begin(this, "Реестр НОПРИЗ");          // / первой строкой: статистика + свежие справочники

    var project = Report.RequireParam<RObject>(this, "Project", "Выберите проект");

    LongRunning.Start(this, () =>
    {
        DataSource = Report.Find("pir_specialistnopriz", inContext: project.Id)
            .Where(o => o.IsAlive() && !o.IsTrainingProject())
            .OrderBy(o => o.Title)
            .Select(o => new Row(o))
            .ToList();
    });
}

private class Row
{
    private readonly RObject _obj;
    public Row(RObject obj) { _obj = obj; }

    public string Name       => _obj.Str("name");
    public string Position   => _obj.Positions("pir_specialistnopriz");
    public string Executors  => _obj.People("executor", shortNames: true);
    public string Deadline   => _obj.DateStr("deadlineDate");
    public string State      => _obj.StateTitle();
    public string Project    => _obj.Project()?.Str("name");
    public string Link       => _obj.Link();
}
```

Для сравнения, тот же код без модуля занимал 100-300 строк служебных функций в каждом отчёте.

> **`using Reports;` работает, только если модуль установлен у пользователя.** Без модуля скрипт не скомпилируется и отчёт не откроется. Поэтому:
> - новые отчёты на модуле публикуйте после того, как модуль назначен всем пользователям на сервере;
> - чтобы подключить **только статистику** к старым отчётам, используйте «мягкую» строку (см. [5.3](#53-подключение-статистики-к-существующим-отчётам)). Она модуль не требует.

---

## 2. Как устроены отчёты в Pilot

### 2.1. Где живут отчёты

Отчёты - объекты базы Pilot в папке отчётов (тип `Report_Folder`). Внутри папки лежат тематические подпапки: `01-…` … `13-Отчеты Заказчика`, `XX-В РАЗРАБОТКЕ`, `XX-ARCHIVE`. Каждый отчёт - это объект с файлом `.repx`, то есть XML-разметкой DevExpress XtraReports 20.1. В разметке лежат макет отчёта, параметры и C#-скрипт в атрибуте `ScriptsSource`.

- Открыть или построить отчёт: двойной щелчок по отчёту или контекстное меню объекта / «Отчёты».
- Редактировать: «Редактировать отчёт» открывает дизайнер DevExpress внутри Pilot. Скрипт находится на вкладке **Scripts**.
- Изменения в отчётах уходят в GitLab этим модулем при сохранении, см. [раздел 6](#6-отправка-отчётов-в-gitlab-cicd).

### 2.2. Жизненный цикл построения

```
Открытие отчёта ──параметры (окно ввода) ──PilotReport_DataSourceDemanded  (UI-поток)
                                                  └─ LongRunning.Start(this, …)   (фоновый поток, UI не зависает)
                                                        └─ DataSource = …
                  ──построение страниц: BeforePrint / PrintOnPage / *_GetValue у полей
                  ──готово (или окно с ошибкой)
```

| Событие | Когда | Типичное использование |
|---|---|---|
| `PilotReport_ParametersRequestBeforeShow` | перед показом окна параметров | заполнить выпадающие списки, предвыбрать значения |
| `PilotReport_DataSourceDemanded` | перед построением | собрать данные и присвоить `DataSource` |
| `<поле>_GetValue` | вычисляемое поле (Calculated Field) | значение ячейки из текущей строки |
| `<контрол>_BeforePrint` | перед выводом контрола | скрыть, покрасить, поменять текст |
| `<контрол>_PrintOnPage` | контрол попал на страницу | нумерация, водяные знаки, «продолжение таблицы» |

### 2.3. Что доступно в скрипте

Скрипт компилируется DevExpress. Ему доступны **все сборки, загруженные в клиент Pilot** (DevExpress берёт их через `CompilerHelper.GetDomainAssemblies`), в том числе сборки модулей. Поэтому `using Reports;` работает без дополнительных настроек. Pilot сам дописывает в начало скрипта `using Ascon.Pilot.DataClasses; …ClientCore.Search; …ProjectionsRepository; …Common.Search;`.

Основные типы (`Ascon.Pilot.Pilot.Reports`):

| Тип | Что это |
|---|---|
| `ReportContext` | доступ к данным: `GetObjects(builder, contextId)`, `GetObject(id)`, `GetType(name)`, `GetTypes()`, `GetUserStates()`, `GetOrganisationUnits()`, `GetPerson(id)`, `CurrentPerson`, `GetPilotUrl(id, null)`, `OpenFile(file)`, `LoadMessages(chatId, …)` |
| `RObject` | объект: `Id`, `Title`, `Type`, `Parent`, `Children`, `Attributes`, `Relations`, `HistoryItems`, `Files`, `State` (живой/заморожен), `IsDeleted`, `IsInRecycleBin`, `IsSecret`, `Creator`, `CreatedUtc` |
| `RType` | тип: `Id`, `Name` (системное имя), `Title`, `Attributes`, `Children` |
| `ROrganisationUnit` | оргединица: `Id`, `Title`, `IsPosition`, `Person`, `VicePersons`, `Children`. **Ссылки на родителя нет** - используйте `Report.ParentUnit` / `Org.DepartmentOf` |
| `RPerson` | человек: `DisplayName`, `Login`, `MainPosition`, `Positions`, `Email` |
| `RRelation` | связь: `Type`, `Name`, `Target` (**может быть null**) |
| `RHistoryItem` | версия объекта: `Created`, `CreatorId`, `Reason`, `Object` (объект на момент версии) |
| `QueryBuilder` | `CreateObjectQueryBuilder()`, `CreateObjectQueryBuilder(ObjectState[])`, `CreateEmptyQueryBuilder()` |
| `LongRunning` | `LongRunning.Start(this, () => …)` - выполнить долгую работу в фоне. Тип клиента Pilot, в `lib/pilot` его нет: модуль на него не ссылается, скрипт видит его в самом клиенте |
| `ReportException` | `throw new ReportException("Выберите проект")` - остановить построение и показать текст пользователю |

### 2.4. Параметры отчёта

Параметры создаются в дизайнере: **Field List / Parameters**. Типы параметров в текущих отчётах:

| Тип параметра | Сколько раз встречается | Как получить значение |
|---|---|---|
| `RObject` | 280 | `(RObject)Parameters["Project"].Value` или `Report.RequireParam<RObject>(this, "Project", "Выберите проект")` |
| `Boolean` | 43 | `Report.Param<bool>(this, "onlyMine")` |
| `ROrganisationUnit` | 25 | `Report.Param<ROrganisationUnit>(this, "department")` |
| `DateTime` | 15 | `Report.Param<DateTime>(this, "dateFrom")` |

Если отчёт запущен из контекстного меню объекта, Pilot сам подставляет выбранный объект в параметр подходящего типа. Если такого параметра нет, Pilot добавляет новый.

---

## 3. Приёмы и подводные камни

### Поиск объектов

```csharp
// Живые и замороженные объекты типа (обычный CreateObjectQueryBuilder() замороженные не находит)
var docs = Report.Find("8_approval_section_doc_rd", inContext: project.Id);

// Поиск по статусу
var inWork = Report.FindByState("task_approval", "in_work");

// Поиск по исполнителю - все должности отдела
var dept = Report.Param<ROrganisationUnit>(this, "department");
var tasks = Report.FindByOrgUnit("task_approval", "executor", Org.PositionIds(dept));

// Любые условия вручную
var list = Report.Find("task_approval", b =>
{
    b.Must(AttributeFields.DateTime("dateOfAssignment").BeInRange(from.ToUniversalTime(), to.ToUniversalTime()));
    b.Must(AttributeFields.State("state").Be(Report.StateByName("in_work").Id));
});
```

Если вызывать `QueryBuilder` напрямую, пригодятся:

- `builder.Must(ObjectFields.ObjectState.BeAnyOf(ObjectState.Alive, ObjectState.Frozen))` - живые и замороженные;
- `builder.Must(AttributeFields.OrgUnit("executor").BeAnyOf(ids))` - поиск по оргединице;
- `builder.Must(AttributeFields.State("state").Be(state.Id))` - поиск по статусу;
- `context.GetObjects(builder, contextId)` - второй параметр задаёт поиск внутри папки (рекурсивно).

### Связи - всегда проверяйте null

У `Relations` бывают `null`-элементы, а у связи - `null` в `Target`, например если нет прав или объект удалён:

```csharp
// было
var attachs = obj.Relations.Where(x => x != null && x.Type == RelationType.TaskAttachments)
                           .Select(x => x.Target).Where(t => t != null).ToList();
// стало
var attachs = obj.Related(RelationType.TaskAttachments);
var contract = folder.RelatedOne("project_contract");
```

### История (предыдущие версии)

`obj.HistoryItems` - версии объекта; `item.Object` - объект на момент версии. Так ищут, когда задание перешло в нужный статус:

```csharp
DateTime? done = task.LastDateInState("Выполнено");   // по имени или заголовку состояния
```

### Заморожен ли объект

`obj.State == ObjectState.Frozen` или `obj.IsFrozen()`.

### Объединение ячеек по столбцам

Нужны три условия одновременно:

1. Текст в соседних ячейках совпадает.
2. Данные подаются через DetailReport: `DetailReport.DataMember = "Docs";`.
3. У столбца (ячейки) стоит **Process Duplicates Mode = Merge**.

Пример - «Верификация КУП», первый отчёт в первой папке.

### Динамический список значений параметра перед построением

```csharp
private void PilotReport_ParametersRequestBeforeShow(object sender, ParametersRequestEventArgs e)
{
    var items = Report.Context.GetTypes()
        .Where(t => t.Name.Contains("task_"))
        .Select(t => ReportParams.Item(t.Name, t.Title));

    // Параметр должен быть мультивыборным, если нужно предвыбрать несколько значений
    ReportParams.SetLookUp(this, "parameter2", items,
        selected: new[] { "task_approval", "task_approval_exp", "task_approval_gip" });
}
```

### Гиперссылка на объект

Свойство строки `public string Link => _obj.Link();`. В дизайнере у ячейки задайте **Navigation URL** = `[Link]`. Клик откроет объект в Pilot.

### Цвет по условию

```csharp
// using System.Drawing;
public Color DeadlineColor =>
    _obj.Date("deadlineDate") is DateTime d && d < DateTime.Now ? Color.Red : Color.Black;
```

В дизайнере: **Expression Bindings / ForeColor** = `[DeadlineColor]`.

### Сортировка шифров

`"1.10"` при обычной сортировке строк встаёт раньше `"1.2"`. Используйте `Text.CodeComparer`:

```csharp
rows.OrderBy(r => r.Code, Text.CodeComparer)
```

### Учебные проекты

Обычно их исключают: `.Where(o => !o.IsTrainingProject())` (папки «Учебные проекты» и «Шаблоны проектов»).

---

## 4. Справочник API модуля

Пространство имён - `Reports`. Все расширения `RObject` безопасны к `null`: объекта нет, атрибута нет или значение `null` - вернётся `""`/`null`/пустой список, исключения не будет.

### Что было в отчётах / что использовать

Частота - в скольких из 196 отчётов со скриптами встречается функция (анализ базы, сентябрь 2026).

| Было в отчётах | Отчётов | Стало |
|---|---:|---|
| `GetStringAttributeValue`, `GetStringAttribute`, `GetStringAttributeByObject`, `GetAttributeStrOrEmpty` | 121+ | `obj.Str("name")`, `obj.StrOrNull("name")` |
| `GetOrgUnitAttributeValue` | 90 | `obj.People("executor")`, `obj.People("executor", shortNames: true)`, `obj.Persons(...)`, `obj.Positions(...)`, `obj.OrgUnitIds(...)` |
| `StatusDocument_GetValue`, `GetStateTitle`, `GetTaskStateName` | 34+ | `obj.StateTitle()`, `obj.StateName()`, `obj.State()`, `Report.StateByName("in_work")` |
| `GetParentByType`, `GetParentByTypes`, `GetParent` | 33+ | `obj.ParentOfType("0_project", …)`, `obj.SelfOrParentOfType(...)`, `obj.Ancestors()` |
| `GetDateTimeAttribute`, `GetDateTimeAttributeString` | 33+ | `obj.Date("deadlineDate")`, `obj.DateStr("deadlineDate")` |
| `GetProject`, `GetMainProjectFolder` | 26+ | `obj.Project()` |
| `GetFio`, `GetShortName` | 20 | `Text.Fio("Иванов Иван Иванович")` / `И.И. Иванов`, `Text.ShortName(...)` / `Иванов И.И.` |
| `IsTrainingProject` | 18 | `obj.IsTrainingProject()`, `obj.IsUnderFolder("…")` |
| `OrganisationUnitService`/`Wrapper`, `FiilDictOrg`, `GetPositions`, `GetOrganisationUnitByTitle` | 12 | `Org.DepartmentOf(person)`, `Org.DepartmentByPersonName()`, `Org.PositionIds(unit)`, `Org.PeopleIn(unit)`, `Org.UnitByTitle(...)` |
| `CleanTitle` | 12 | `Text.CleanTitle(...)` |
| `CompareByDigitsCode` | 6 | `Text.CompareCodes(a, b)`, `Text.CodeComparer` |
| поиск через `QueryBuilder` + `context.GetObjects` | 150 | `Report.Find/FindByAttr/FindByState/FindByOrgUnit/Query` |
| `(T)Parameters["x"].Value` + проверка на null + `ReportException` | 60+ | `Report.Param<T>`, `Report.RequireParam<T>` |
| `context.GetUserStates().FirstOrDefault(...)` в каждом месте | 117 | `Report.State(id)`, `Report.StateByName(...)` (кэш на построение) |

### `Report` - вход, справочники, поиск, параметры

| Член | Описание |
|---|---|
| `Begin(this, "Имя отчёта", reportId?)` | Первая строка `DataSourceDemanded`: начинает учёт построения и сбрасывает кэши. Исключений не бросает |
| `Context` | общий `ReportContext` модуля |
| `OrgUnits`, `OrgUnit(id)`, `ParentUnit(id)` | оргструктура (кэш на построение) |
| `States`, `State(id)`, `StateByName(nameOrTitle)` | пользовательские состояния |
| `Type(name)`, `RequireType(name)` | типы; `RequireType` бросает `ReportException`, если типа нет |
| `Find(type(s), filter?, inContext?, includeFrozen = true)` | поиск по типу с доп. условиями |
| `FindByAttr`, `FindByState`, `FindByOrgUnit`, `Query` | готовые виды поиска |
| `Param<T>(this, name)`, `RequireParam<T>(this, name, message)` | параметры отчёта |

### Расширения `RObject` (`RObjectExtensions`)

| Группа | Методы |
|---|---|
| Атрибуты | `Attr`, `Attr<T>`, `Has`, `Str`, `StrOrNull`, `Date`, `DateStr(name, format)`, `Int`, `Double`, `Name()` |
| Оргединицы в атрибутах | `OrgUnitIds`, `OrgUnits`, `Persons`, `People(name, separator, shortNames)`, `Positions` |
| Состояния | `StateId`, `State`, `StateTitle`, `StateName`, `LastDateInState(state)` |
| Иерархия | `IsAlive`, `IsFrozen`, `Ancestors`, `ParentOfType`, `SelfOrParentOfType`, `Project`, `IsUnderFolder`, `IsTrainingProject`, `AliveChildren(types…)` |
| Связи | `Related(RelationType?, types…)`, `RelatedOne(types…)` |
| Прочее | `Link()` - ссылка для Navigation URL |

### `Org`, `Text`, `ReportParams`

- `Org`: `DepartmentOf(positionId | person)`, `DepartmentTitle(person)`, `DepartmentByPersonName()`, `PositionIds(unit)`, `PeopleIn(unit)`, `UnitByTitle(title)`.
- `Text`: `Fio`, `ShortName`, `CleanTitle`, `JoinNonEmpty`, `CompareCodes`, `CodeComparer`.
- `ReportParams`: `Get<T>`, `Require<T>`, `GetValue`, `SetValue`, `SetLookUp(this, name, items, selected)`, `Item(value, description)`, `FindParameter`. Работает через рефлексию, поэтому не зависит от версии DevExpress.

### `Pilot` - Pilot SDK из отчёта

| Член | Описание |
|---|---|
| `Repository`, `Search`, `Modifier`, `Files`, `Dialogs`, `Tabs`, `ClientInfo` | сервисы SDK клиентского модуля |
| `IsReady` | модуль загружен и сервисы доступны |
| `CurrentPerson`, `GetType(name)` | текущий пользователь, тип SDK |
| `LoadObject(id)`, `LoadObjects(ids)` | синхронная загрузка `IDataObject` (с таймаутом, по умолчанию 60 с) |
| `SearchIds(b => …)`, `SearchObjects(b => …)` | серверный поиск через `ISearchService` |

```csharp
// Файлы документа через SDK (в ReportContext такого нет)
var doc = PilotApi.LoadObject(row.Id);
foreach (var file in doc.ActualFileSnapshot.Files)
    using (var stream = PilotApi.Files.OpenRead(file)) { /* … */ }

// Кто строит отчёт
var me = PilotApi.CurrentPerson.DisplayName;
```

`Load*`/`Search*` можно вызывать и в `DataSourceDemanded` (UI-поток), и внутри `LongRunning.Start` (фон). Модуль сам ждёт ответа SDK так, чтобы не заблокировать интерфейс.

> `PilotApi.Modifier` позволяет **менять данные** из отчёта. Отчёт - инструмент чтения. Изменения делайте осознанно и только в отчётах, которые для этого предназначены.

---

## 5. Статистика построений

### 5.1. Что считается

Каждое построение - одна запись со статусом:

| Статус | Когда | В Grafana |
|---|---|---|
| `success` | страницы построены (`PrintingSystem.AfterBuildPages`) | успешное построение |
| `error` | исключение в скрипте или при построении | ошибка - надо чинить отчёт |
| `validation` | скрипт сам остановил построение через `ReportException` («Выберите проект») | не ошибка отчёта |
| `cancelled` | окно закрыли до конца построения или перестроили с другими параметрами | не учитывается в доле успешных |

**Кол-во построений** - все записи. **Кол-во удачных** - `success`. **Доля успешных** = `success / (все − cancelled)`.

Вместе со статусом отправляются: имя отчёта, время начала, длительность, число страниц, тип и текст ошибки, логин и ФИО пользователя, адрес сервера Pilot (по нему сервис определяет контур prod/test/stage), тип и версия клиента, версия модуля, имя компьютера. Параметры отчёта и данные из него **не отправляются**. Исключение - текст ошибки: это сообщение корневого исключения (до 1000 символов). Если скрипт кладёт в сообщение данные из базы (`throw new Exception($"…{значение}")`), они уйдут вместе с ним, поэтому данные объектов в сообщения не подставляйте.

### 5.2. Настройка

`Reports.config.json` лежит рядом с dll и входит в zip модуля. В репозитории адреса в нём пустые (`statsUrl`, `reportsGitLabGroupUrl`, `gitLabOAuthClientId`): впишите свои до сборки или правьте файл на сервере Pilot. Пока `statsUrl` пуст, статистика выключена, пока пуст `reportsGitLabGroupUrl`, отправка в GitLab недоступна. Пример значений:

```json
{
  "statsEnabled": true,
  "statsUrl": "http://stats.example.local:5310/api/report-events",
  "apiKey": "",
  "reportsGitLabGroupUrl": "http://gitlab.example.local:8080/reports",
  "publishOnSave": true,
  "gitLabOAuthClientId": "",
  "gitLabOAuthRedirectUri": "http://127.0.0.1:7890/callback",
  "useGitCredentials": true
}
```

| Поле | Назначение |
|---|---|
| `statsEnabled` | `false` - статистика не собирается |
| `statsUrl` | адрес сервиса [report-metrics-collector](https://github.com/PilotOpenTools/PilotProAdmin/blob/main/stack/metrics/report-collector/README.md), доступный с рабочих мест (не `localhost`). Пусто - статистика выключена. Переопределяется общими настройками «Модули» и переменной окружения `REPORTS_STATS_URL` |
| `apiKey` | если в сервисе задан `COLLECTOR_API_KEY` - тот же ключ |
| `reportsGitLabGroupUrl` | группа GitLab с отчётами - в ней ищется репозиторий при сохранении отчёта (см. раздел 6) |
| `publishOnSave` | `false` - окно отправки в GitLab при сохранении отчёта не показывается |
| `gitLabOAuthClientId` | Application ID приложения GitLab для входа через браузер (см. раздел 6). Пусто - кнопки «Войти через браузер» нет (в репозитории пусто: идентификатор приложения своего GitLab впишите сами) |
| `gitLabOAuthRedirectUri` | локальный адрес возврата из браузера, должен совпадать с Redirect URI приложения |
| `useGitCredentials` | `false` - не брать токен из менеджера учётных данных git |

Статистику переопределяет раздел **«PilotOpenTools отправка статистики»** общих настроек Pilot: поля «Отправлять статистику», «Адрес сервиса статистики» и «API-ключ». Раздел задаётся на каждом контуре и применяется без перезапуска клиента. В нём указан адрес статистики модулей (`…/api/module-events`), отчёты отправляют на тот же сервис в `/api/report-events`. Пока раздел пуст, действует файл.

GitLab задаётся в своём разделе общих настроек Pilot **«PilotOpenTools отчёты (GitLab)»** (Сервис / Настройки / Общие настройки, его добавляет этот модуль). Пересобирать модуль ради адреса не нужно:

| Поле раздела | Поле файла |
|---|---|
| Адрес сервера GitLab: `http://сервер:порт`, без пути. Кнопка «Проверить» проверяет, что сервер доступен и группа видна | `reportsGitLabGroupUrl`, часть до пути |
| Группа с отчётами: `reports` или `pilot/reports` | `reportsGitLabGroupUrl`, путь |
| Предлагать отправить в GitLab при сохранении | `publishOnSave` |
| Application ID и Redirect URI для входа через браузер | `gitLabOAuthClientId`, `gitLabOAuthRedirectUri` |
| Брать токен из менеджера учётных данных git | `useGitCredentials` |

Раздел важнее файла и применяется у пользователей сразу. Пока он пуст, действуют значения из файла, кнопка «Очистить раздел» возвращает к ним. Токены в раздел не пишутся: каждый пользователь входит в GitLab сам. Пока ни в разделе, ни в файле нет сервера и группы, окно отправки при сохранении не открывается, а в `Reports.log` пишется строка «Отчёт сохранён без окна отправки в GitLab: …».

На машине разработчика адрес можно переопределить переменной окружения `REPORTS_STATS_URL`, она важнее общих настроек.

Каждое построение отправляется отдельным запросом сразу после окончания (примерно через 1 с), в фоне - построение отчёта сеть **не ждёт**. Очереди нет: если сервис недоступен, событие пишется в `Reports.log` и теряется. Отчёты строят редко, копить нечего. Если часы на рабочем месте сбиты, сервис поправит время по заголовку `X-Client-Time`.

### 5.3. Подключение статистики к существующим отчётам

Есть два способа.

**Новые и переписываемые отчёты** - первой строкой `DataSourceDemanded`:

```csharp
Report.Begin(this, "Название отчёта как в Pilot");
```

**Существующие отчёты без переписывания** - «мягкая» строка через рефлексию. Если модуля нет, она ничего не делает и отчёт не ломает. Вставлять её вручную не нужно - это делает скрипт:

```bash
python tools/add_report_stats.py "Реестр НОПРИЗ.repx"                  # имя отчёта = имя файла
python tools/add_report_stats.py report.repx --name "Реестр НОПРИЗ"    # имя явно
python tools/add_report_stats.py ./reports --dry-run                   # все .repx в папке, без записи
```

Скрипт меняет в `.repx` только место вставки, остальное остаётся байт в байт. Повторный запуск пропускает уже подключённые отчёты (метка `Reports:stats`). Проверено на всех 204 отчётах базы: изменены 196, у 8 нет скрипта. Отредактированный `.repx` загружается обратно в Pilot через дизайнер отчёта (открыть файл и сохранить отчёт) или через MCP `UpdateReport`. Начните с одного-двух отчётов на test/stage.

Вставляемая строка:

```csharp
// Reports:stats - статистика построений (Grafana). Без модуля Reports строка ничего не делает.
try { foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType("Reports.Report"); if (t != null) { t.GetMethod("Begin").Invoke(null, new object[] { this, "Имя отчёта", null }); break; } } } catch { }
```

### 5.4. Где смотреть

Grafana / дашборд **«Pilot - отчёты»**: плитки (построений, успешных, доля, ошибок, пользователей), сводка по отчётам, графики по статусам и по отчётам, сравнение с прошлым периодом, пользователи, самые долгие отчёты, последние ошибки с текстом исключения. Установка - в [README сервиса](https://github.com/PilotOpenTools/PilotProAdmin/blob/main/stack/metrics/report-collector/README.md#grafana).

---

## 6. Отправка отчётов в GitLab (CI/CD)

Отчёты живут в GitLab так же, как автоматизации:

| Ветка | Сервер Pilot |
|---|---|
| `dev` | тестовый, а при настройке админки «Отчёты делают на стейдже» стейдж |
| `hotfix/<дата-время>` | нет, срочное исправление прода от `main` |
| `main` | продовый |

Стейджа и веток `release/*` у отчётов нет.

**Один отчёт = один репозиторий** в группе отчётов (`reportsGitLabGroupUrl`, например `http://localhost:8080/repports`). Подгруппы работают как папки. **Имя проекта GitLab = имя отчёта в Pilot**. Регистр, `ё/е` и символы, недопустимые в имени проекта GitLab (`( ) # " :` …), при сравнении не учитываются. Путь проекта (slug) - любой, например «Отчет по замороженным элементам» / `repports/01-folder/frozen-element`. Файл - `Имя отчёта.repx` в корне. Если в репозитории единственный `.repx`, подходит и он (так было в модуле 043).

### Как это выглядит

1. Отчёт правится в дизайнере Pilot и сохраняется.
2. Модуль показывает окно:
   - найденный репозиторий и его ветки;
   - выбор ветки **dev - тест** или **hotfix прода** - ветка `hotfix/<дата-время>` от `main` (по умолчанию `dev`, для hotfix есть подтверждение); прямо в `main` модуль не отправляет;
   - поле для описания изменения.
3. **«Сохранить и отправить»** - `.repx` уходит в выбранную ветку одним коммитом «Имя отчёта: описание». Файл такой, каким его записал дизайнер, но без хвоста после первого `</XtraReportsLayoutSerializer>`: дизайнер Pilot пишет новый отчёт поверх старого файла без обрезки, и если отчёт стал короче, остаётся хвост старого файла. Модуль его убирает и пишет об этом в лог. Если в ветке уже этот файл, коммит не создаётся.
   - Если в «Что изменилось» введена причина, сохранение дизайнера отменяется, а тот же файл сразу записывается в Pilot новой версией с этой причиной (SDK не позволяет задать причину версии, созданной дизайнером). При сохранении нескольких отчётов сразу причина в версию Pilot не попадает.
   - Тем же коммитом описание дописывается в `CHANGELOG.md` (каждая строка - отдельный пункт): в раздел «Не выпущено» (и для `dev`, и для hotfix). Формат тот же, что у веб-админки; при переносе на прод она соберёт пункты «Не выпущено» в раздел с датой.
   - **«Только в Pilot»** - сохранить без GitLab.
   - **«Отменить сохранение»** - отчёт не сохраняется в Pilot.
   - **«История…»** - коммиты по отчёту в `dev` и `main`.
   - **«Сохранить в файл…»** - копия `.repx` на диск.
4. На прод отчёт переносит веб-админка: **«На прод /»** - Merge Request `dev` / `main`, pipeline, запись на продовый сервер и merge. Hotfix - кнопкой **«Hotfix в прод»** на странице отчёта, после чего **«/ В тест»** вернёт его в `dev`.

Если репозитория нет, окно так и скажет. Создать его можно в веб-админке: «Отчёты» / «создать репозиторий», там же выбирается папка. `main` получит отчёт с прода, `dev` - с теста.

### Вход в GitLab

Модуль работает через REST API GitLab, локальный `git` не нужен. Коммиты идут **от имени того, кто вошёл**. В окне отправки видно: «GitLab: login · способ входа · пользователь Pilot».

**Способы входа:**

1. **«Войти через браузер»** (OAuth, как в веб-админке). Открывается GitLab, пользователь входит своим паролем или через SSO и разрешает доступ. Браузер возвращает код на локальный адрес `http://127.0.0.1:7890/callback`, модуль получает токен. Пароль в Pilot не вводится. Используется OAuth 2.0 Authorization Code + PKCE: секрета приложения нет, в zip модуля хранить нечего. OAuth-токен живёт 2 часа и обновляется сам по refresh-токену. Если обновить не удалось, модуль попросит войти снова.
2. **«Токен GitLab…»** - Personal Access Token со scope `api` (GitLab / Edit profile / Access Tokens).
3. Если своего входа нет - пароль или токен из менеджера учётных данных git (`git credential fill`), если пользователь уже работал с этим GitLab через git. `git.exe` ищется только в каталогах `PATH`, ответ ждётся не дольше 5 с. Выключается `useGitCredentials: false`.

Адрес GitLab берётся из `reportsGitLabGroupUrl`. Токен передаётся в заголовке `Authorization: Bearer`, поэтому при `http://` он идёт по сети без шифрования: модуль пишет об этом предупреждение в лог. Для рабочего стенда включите https и укажите https-адрес. Ссылки из ответов GitLab (репозиторий, коммит) модуль открывает в браузере только со схемой http или https.

**«Выйти»** удаляет сохранённый токен этого пользователя с компьютера. Токен, отклонённый GitLab (401), удаляется автоматически.

#### Токен у каждого пользователя свой

- Файл токена лежит в профиле Windows: `%AppData%\ASCON\Reports\gitlab\`. У каждой учётной записи Windows он свой.
- Файл зашифрован **DPAPI (CurrentUser)**. Другая учётная запись Windows его не расшифрует, даже если скопирует.
- Имя файла - хеш **логина Pilot и адреса GitLab**. Если под одной учётной записью Windows работают разные пользователи Pilot (общий компьютер, терминальный сервер), у каждого свой токен.
- Исключение: менеджер учётных данных git хранит данные на уровне учётной записи Windows, а не пользователя Pilot. Если на общем компьютере под одной учётной записью Windows работают разные люди, поставьте `useGitCredentials: false`.

#### Настройка входа через браузер (один раз, администратор GitLab)

1. GitLab / **Admin Area / Applications / New application** (или User Settings / Applications):
   - Name: `Reports (Pilot)`;
   - Redirect URI: `http://127.0.0.1:7890/callback`;
   - **Confidential: снять галочку** - приложение публичное, работает с PKCE;
   - Scopes: `api`.
2. Application ID вписать в общие настройки «PilotOpenTools отчёты (GitLab)» (или в `Reports.config.json` / `gitLabOAuthClientId`, тогда модуль нужно пересобрать и загрузить на сервер Pilot). Secret не нужен и в модуль не кладётся.
3. Если порт 7890 на рабочих местах занят, поменяйте его и в приложении GitLab, и в Redirect URI раздела (`gitLabOAuthRedirectUri`).

Это отдельное приложение, не то, что у веб-админки: у админки приложение confidential (секрет хранится на сервере), а клиентский модуль секрет хранить не может.

Права: роль **Developer** и право push в выбранную ветку. В `main` модуль не отправляет: на прод отчёт попадает только через веб-админку.

### Переход с 043SendReportToGitlab

- Снимите модуль `043SendReportToGitlab.ext2` с сервера, иначе при сохранении появятся два окна.
- Строку `const string GitLabLink = "…"` из скриптов можно удалить - она больше не используется.
- Переименуйте проекты в GitLab так, чтобы **имя проекта** совпадало с именем отчёта (Settings / General / Project name). Путь менять не нужно.

### Нет репозитория или ветки

- **Нет ветки** `dev`: ветка создаётся при отправке тем же коммитом (`start_branch`) от `main`.
- **Нет репозитория**: в окне отправки и в окне PilotReport появляется кнопка **«Создать репозиторий…»**. Там выбирается папка (подгруппа) группы отчётов. Модуль подсказывает её по папке отчёта в Pilot, например «01-Папка» / «01-folder». Для каждой папки показаны права пользователя GitLab. Создать можно только там, где роль пользователя (с учётом родительских групп) проходит настройку группы «кто может создавать проекты» (`project_creation_level`, по умолчанию Maintainer). Администратор GitLab может создавать везде. Будет создан проект с именем отчёта: в `main` - `README.md` и `CHANGELOG.md`, ветка `dev` от `main`. Сам отчёт уходит следующей отправкой.

### Окно PilotReport (главное меню Pilot)

В главном меню клиента (рядом с «Сервис») есть меню **PilotReport**, в нём пункт «Отчёты: загрузка, сравнение, отправка в GitLab…». Меню видят администраторы Pilot и люди, отмеченные в общих настройках «PilotOpenTools отчёты (GitLab)», блок «Кнопка PilotReport в главном меню». Остальным оно не показывается. Изменение списка у пользователя видно после перезапуска клиента. Слева в окне дерево папок отчётов с поиском. Справа кнопки для выбранного отчёта:

| Кнопка | Что делает |
|---|---|
| **Загрузить .repx с диска…** | Открывает проводник и проверяет, что файл - отчёт DevExpress. Затем спрашивает **причину** и создаёт новую версию отчёта в Pilot (`CreateFileSnapshot(reason)`). После этого открывается обычное окно отправки в GitLab, причина уже стоит в «Что изменилось». |
| **Сравнить с main / dev** | Diff отчёта в Pilot с файлом ветки. Скрипт (`ScriptsSource`) и разметка сравниваются на разных вкладках. «−» - только в Pilot, «+» - придёт из ветки. Из окна сравнения можно сразу обновить отчёт. |
| **Обновить из main / dev…** | Берёт `.repx` из ветки, спрашивает причину (по умолчанию «Обновлено из GitLab main (sha: коммит)») и создаёт новую версию в Pilot. Окно отправки в GitLab при этом не появляется: версия и так из GitLab. |
| **Отправить в dev… / Отправить в main…** | Отправляет текущую версию отчёта из Pilot в GitLab: открывается окно отправки (как при сохранении в дизайнере, но в Pilot ничего не сохраняется). «main» - hotfix прода: ветка `hotfix/…` от `main`, на прод её выпускает веб-админка. Кнопки доступны, когда репозиторий отчёта найден; недостающая ветка создаётся при отправке. |
| Сохранить .repx на диск… | Скачивает текущую версию из Pilot. |

Внизу показана история версий отчёта в Pilot: дата, автор, причина. Кнопки работают только для отчёта, у которого в группе GitLab есть репозиторий и файл отчёта в нужной ветке. Если файл совпадает с текущей версией байт в байт, новая версия не создаётся.

## 7. Сборка, отладка, выпуск

Требования: .NET SDK 8+ (собирает `net48`; в CI - SDK 10.0.404), Windows для отладки в клиенте и для тестов. Для компиляции нужны сборки клиента Pilot: установленный Pilot-BIM, каталог из `PILOT_CLIENT_DIR` или копия в `lib/pilot` - см. [Сборки клиента Pilot](#сборки-клиента-pilot-в-libpilot).

```bash
dotnet build -c Debug          # + копия в %LocalAppData%\ASCON\Pilot-BIM\Development\112Reports.ext2
dotnet build -c Release        # bin\112Reports.ext2-<версия>.zip - для загрузки на сервер
dotnet test tests/Reports.Tests -c Release
```

#### Источник пакета Pilot.ClientSdk

Пакет `Pilot.ClientSdk` (только для компиляции) лежит не в nuget.org, а в реестре пакетов GitLab. В `nuget.config` источник `cicd` указан примером: `http://gitlab.example.local:8080/api/v4/projects/9/packages/nuget/index.json`. Замените его в файле на адрес реестра своего GitLab (проект с пакетом ClientSdk). CI входит в этот источник токеном job автоматически, на своей машине добавьте вход личным токеном (подсказка в комментарии `nuget.config`).

Чистый клон без доступа к вашему GitLab собирается так (так же собирает GitHub Actions):

1. Скачайте пакет из релиза [PilotClientSdk](https://github.com/PilotOpenTools/PilotClientSdk/releases) и создайте `nuget.local.config`, в котором источник `cicd` указывает на локальную папку `local-pkg` (оба пути в `.gitignore`):
   ```powershell
   ./tools/get-clientsdk.ps1
   ```
   Если релиза с версией из `Reports.csproj` нет, скрипт берёт последний, и NuGet выбирает ближайшую большую версию (предупреждение NU1603). Пакет можно собрать и самому из исходников PilotClientSdk: `dotnet pack Pilot.ClientSdk.csproj -c Release -o <папка>`.
2. Передайте конфиг при восстановлении: `dotnet test Reports.sln -c Release -p:RestoreConfigFile=nuget.local.config`.

Источник `cicd` описан с `allowInsecureConnections="true"`, потому что GitLab стенда по умолчанию работает по http. Если у вас https, уберите этот атрибут.

- **Отладка.** Debug-сборка кладёт модуль в папку разработки Pilot-BIM. Клиент подхватывает его при запуске без загрузки на сервер. Запуск из IDE стартует `Ascon.Pilot.PilotBIM.exe`, точки останова работают. Чтобы не копировать в клиент: `dotnet build -p:CopyToPilot=false`.
- **Выпуск - через CI/CD модулей.** Репозиторий `client-modules/module-for-report`, вкладка «Модули» в веб-админке: `dev` / тест, `release/X.Y.Z` / стейдж, `main` / прод, см. [README модулей](https://github.com/PilotOpenTools/PilotProAdmin/blob/main/client-modules/README.md). Версия задаётся только в `<Version>` файла `Reports.csproj`, её и `CHANGELOG.md` поднимает админка. Модуль, которого не было на сервере, нужно назначить пользователям в Pilot-myAdmin. Выкладку из CI админка принимает по ключу контура (`DEPLOY_KEY_TEST`, `DEPLOY_KEY_STAGE`, `DEPLOY_KEY_PROD` в переменных группы GitLab; stage и prod - Masked и Protected) и по токену задания `CI_JOB_TOKEN`; ветки `main` и `release/*` должны быть защищены. Подробности - в README модулей.
- **CI.** `.gitlab-ci.yml` по общему шаблону собирает Release на Linux-раннере и кладёт zip в артефакты; перед сборкой скачивает сборки клиента Pilot из реестра пакетов проекта (`PILOT_CLIENT_VERSION`, см. выше). Тесты `net48` (WPF, DPAPI) на Linux не запускаются. Для них в `.gitlab-ci.yml` есть задание `test_module` на Windows-раннере с тегом `windows` (shell этого раннера должен быть `powershell`): оно включается переменной группы `WINDOWS_RUNNER=true`, а когда включено, выкладка (`deploy_*`) ждёт его успешного завершения; сборки клиента Pilot задание берёт из того же пакета реестра. Без Windows-раннера гоняйте тесты локально перед слиянием в `dev`. `.gitlab-ci.yml` модуля отличается от общего шаблона (`test_module`, скачивание пакета `pilot-client`), поэтому кнопку «Обновить CI» на вкладке «Модули» для этого модуля не нажимайте: она заменит файл шаблоном, и `build_module` упадёт без `lib/pilot`. Скачивание пакета в шаблон пока не перенесено.
- **Тесты** (`dotnet test Reports.sln` тоже их запускает): форматирование ФИО и шифров, атрибуты объектов (`Str`, `Date`, `Int`, `Double`, `OrgUnitIds`, `StateId`), параметры отчёта (`ReportParams`), безопасное открытие ссылок, поиск `git.exe`, JSON событий, конфиг, классификация ошибок, полный цикл учёта построения (фейковый отчёт / HTTP сразу после построения), «мягкая» строка, сопоставление отчёта с проектом по имени и отправка `.repx` на фейковом GitLab, раздельное хранение токенов по пользователям, полный вход через браузер (OAuth + PKCE) на фейковом GitLab. Интеграционный тест с настоящим сервисом запускается, если задан `REPORTS_IT_URL`, иначе он пропущен (в отчёте xUnit - «Skipped»). Не покрыты тестами окна и модели представления (`PublishViewModel`, `ReportManagerViewModel`) и `ReportSaveHandler`: они завязаны на WPF и живой клиент Pilot.

### Сборки клиента Pilot в `lib/pilot`

Типов `ReportContext`, `RObject`, `QueryBuilder`, `ObjectFields` нет в NuGet-пакете SDK - они живут в сборках клиента Pilot (шесть dll `Ascon.Pilot.*`, `Private=false`, в zip не попадают). Это проприетарные библиотеки АСКОН, поэтому в репозитории их нет (`lib/pilot/*.dll` в `.gitignore`). Каталог сборок выбирает `PilotClient.props`: переменная `PILOT_CLIENT_DIR`, иначе `lib/pilot` (если там есть `Ascon.Pilot.Pilot.dll`), иначе установленный клиент `C:\Program Files\ASCON\Pilot-BIM`. Нет сборок нигде - сборка падает с понятной ошибкой.

На рабочем месте с клиентом Pilot-BIM ничего настраивать не нужно. Без клиента сборки достаёт из публичного дистрибутива Pilot-BIM скрипт `tools/get-pilot-client.ps1` (его же использует GitHub Actions). В GitLab CI задания `build_module` и `test_module` скачивают сборки из реестра пакетов GitLab этого проекта (пакет `pilot-client`, версия в переменной `PILOT_CLIENT_VERSION`); пакет загружает администратор с машины с клиентом скриптом `tools/publish-pilot-client.ps1`. Подробности и команды - в [lib/README.md](../lib/README.md).

### Структура

```
Reports.csproj, PilotClient.props, AssemblyInfo.cs, Reports.config.json, .gitlab-ci.yml
Pilot/       ModuleStartup (IDataPlugin - точка входа), PilotApi (SDK), MainMenu, CommonStatsSettings
Manager/     окно PilotReport: дерево отчётов, сравнение с ветками, загрузка новых версий
Reports/     Report, RObjectExtensions, Org, Text, ReportParams
Stats/       ReportTracker (учёт построения), ReportStats (отправка), ReportRunEvent (JSON)
Publishing/  ReportSaveHandler (окно при сохранении отчёта), ReportPublishService, GitLabApi, GitLabAuth, GitLabOAuth, GitLabTokenStore, View/
Infrastructure/  ModuleConfig, ModuleLog, ExternalLink
tools/       add_report_stats.py - подключение статистики к .repx; publish-pilot-client.ps1 - загрузка сборок клиента Pilot в реестр пакетов GitLab;
             get-pilot-client.ps1 - сборки клиента из дистрибутива Pilot-BIM; get-clientsdk.ps1 - пакет Pilot.ClientSdk из релиза GitHub
tests/       Reports.Tests (xUnit, net48)
lib/pilot/   сюда кладётся копия сборок клиента Pilot для компиляции (dll в git нет), см. lib/README.md
PilotClient.props   каталог сборок клиента Pilot (общий для модуля и тестов)
```

---

## 8. Как это устроено внутри

- **Загрузка.** `ModuleStartup` экспортируется как `IDataPlugin`, поэтому клиент создаёт его при старте. В конструктор приходят сервисы SDK, он их запоминает и вызывает `StartupRunner.Run`. Уже в `Initialize()` сервисы передаются в `PilotApi` и запускается отправка статистики.
- **Почему отчёт видит модуль.** Модули Pilot грузятся из файлов в тот же AppDomain, что и отчёты. Отдельного домена нет - это проверено по `Ascon.Pilot.PluginLoader`. Компилятор скриптов DevExpress добавляет в ссылки все сборки домена. Pilot также поддерживает директиву `//LOAD:<сборка>` в скрипте (`PatchUsings`), но для модуля она не нужна.
- **Учёт построения.** `Report.Begin` находит `PrintingSystem` отчёта и подписывается на `AfterBuildPages`, `CreateDocumentException` и `Disposed`. Работа идёт через рефлексию, поэтому ссылки на DevExpress нет и обновление DevExpress в клиенте модуль не ломает.
- **Без сторонних зависимостей.** Модуль не тянет NLog, Newtonsoft.Json и System.Reactive: в общем AppDomain клиента разные версии этих библиотек у разных модулей конфликтуют. JSON и лог написаны вручную.
- **SDK из фонового потока.** Подписки SDK создаются на UI-потоке через `Dispatcher.Invoke`. `LongRunning` в это время прокачивает очередь сообщений, поэтому интерфейс не зависает. На UI-потоке ожидание идёт во вложенном `DispatcherFrame`, как в `ObjectLoaderSync` из NugetPackage.

---

## 9. Типовые проблемы

| Симптом | Причина | Решение |
|---|---|---|
| Отчёт не открывается: `The type or namespace name 'Reports' could not be found` | у пользователя нет модуля | назначить модуль пользователю; для статистики в старых отчётах использовать «мягкую» строку |
| `Модуль Reports не загружен клиентом Pilot: сервисы SDK недоступны…` (исключение из `Pilot`) | модуль не стартовал | лог `Reports.log`; модуль назначен на сервере? |
| В Grafana нет данных | неверный адрес статистики или сервис недоступен с рабочего места | `Reports.log` («Статистика … не отправлена»); проверить `http://<хост>:5310/health` с рабочего места |
| Сервис отвечает 401 | не совпадает `apiKey` | поле «API-ключ» в общих настройках «Модули» (или ключ в `Reports.config.json`) = `COLLECTOR_API_KEY` |
| Отчёт в статистике называется `<без имени>` | `Begin` вызван без имени | передавать имя вторым параметром |
| Построение без статуса | `Begin` не вызван или вызван не в `DataSourceDemanded` основного отчёта | первой строкой `PilotReport_DataSourceDemanded` |
| `LoadObjects` вернул меньше объектов | объект удалён, нет прав или истёк таймаут | увеличить `timeout`, проверить права |
