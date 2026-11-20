using System;
using System.Collections.Generic;
using System.Linq;
using Ascon.Pilot.ClientCore.Search;
using Ascon.Pilot.Common.Search;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Pilot.Reports;
using Reports.Infrastructure;
using Reports.Stats;

namespace Reports
{
    /// <summary>
    /// Главная точка входа для скрипта отчёта.
    /// <code>
    /// using Reports;
    ///
    /// private void PilotReport_DataSourceDemanded(object sender, System.EventArgs e)
    /// {
    ///     Report.Begin(this, "Реестр НОПРИЗ");   // статистика + свежие справочники
    ///     LongRunning.Start(this, () =>
    ///     {
    ///         DataSource = Report.Find("pir_specialistnopriz").Where(o => o.IsAlive()).ToList();
    ///     });
    /// }
    /// </code>
    /// Справочники (оргструктура, состояния, типы) кэшируются на время одного построения
    /// и сбрасываются в <see cref="Begin"/>.
    /// </summary>
    public static class Report
    {
        private static readonly object Sync = new object();
        private static ReportContext _context;
        private static Cache _cache;

        /// <summary>
        /// Контекст отчёта модуля. Можно использовать вместо своего <c>new ReportContext()</c>.
        /// </summary>
        public static ReportContext Context
        {
            get
            {
                lock (Sync)
                    return _context ?? (_context = new ReportContext());
            }
        }

        /// <summary>
        /// Вызывать первой строкой в PilotReport_DataSourceDemanded.
        /// Начинает учёт построения (кол-во построений и удачных построений уходит в Grafana)
        /// и сбрасывает кэши справочников. Никогда не бросает исключений.
        /// </summary>
        /// <param name="report">this из скрипта отчёта.</param>
        /// <param name="reportName">Название отчёта для статистики. Лучше указывать явно - так, как он назван в Pilot.</param>
        /// <param name="reportId">GUID объекта отчёта в Pilot, если известен.</param>
        /// <returns>Идентификатор построения (пригодится при разборе логов), Guid.Empty если учёт не начат.</returns>
        public static Guid Begin(object report, string reportName = null, Guid? reportId = null)
        {
            try
            {
                lock (Sync)
                {
                    _cache = null;
                    _context?.InvalidateCache();
                }
                return ReportTracker.Begin(report, reportName, reportId);
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Report.Begin", ex);
                return Guid.Empty;
            }
        }

        // ------------------------------------------------------------------
        // Справочники
        // ------------------------------------------------------------------

        /// <summary>Все оргединицы по Id.</summary>
        public static IReadOnlyDictionary<int, ROrganisationUnit> OrgUnits => GetCache().Units;

        /// <summary>Оргединица по Id или null.</summary>
        public static ROrganisationUnit OrgUnit(int id) => GetCache().Units.TryGetValue(id, out var unit) ? unit : null;

        /// <summary>Родительская оргединица (в ROrganisationUnit ссылки на родителя нет) или null.</summary>
        public static ROrganisationUnit ParentUnit(int unitId) =>
            GetCache().Parents.TryGetValue(unitId, out var parentId) ? OrgUnit(parentId) : null;

        /// <summary>Пользовательские состояния по Id.</summary>
        public static IReadOnlyDictionary<Guid, INUserState> States => GetCache().States;

        /// <summary>Состояние по Id или null.</summary>
        public static INUserState State(Guid id) => GetCache().States.TryGetValue(id, out var state) ? state : null;

        /// <summary>Состояние по системному имени (Name) или заголовку (Title), null если нет.</summary>
        public static INUserState StateByName(string nameOrTitle) =>
            GetCache().States.Values.FirstOrDefault(s => s.Name == nameOrTitle)
            ?? GetCache().States.Values.FirstOrDefault(s => s.Title == nameOrTitle);

        /// <summary>Тип по системному имени или null.</summary>
        public static RType Type(string name) =>
            GetCache().TypesByName.TryGetValue(name ?? string.Empty, out var type) ? type : null;

        /// <summary>Тип по системному имени; если нет - ReportException с понятным текстом.</summary>
        public static RType RequireType(string name) =>
            Type(name) ?? throw new ReportException($"В базе нет типа '{name}'");

        // ------------------------------------------------------------------
        // Поиск
        // ------------------------------------------------------------------

        /// <summary>
        /// Поиск объектов по типу с дополнительными условиями.
        /// По умолчанию ищутся живые и замороженные объекты (замороженные обычный поиск не находит).
        /// <code>
        /// var tasks = Report.Find("task_approval", b =>
        /// {
        ///     b.Must(AttributeFields.State("state").Be(Report.StateByName("in_work").Id));
        ///     b.Must(AttributeFields.OrgUnit("executor").BeAnyOf(positionIds));
        /// }, inContext: project.Id);
        /// </code>
        /// </summary>
        public static IList<RObject> Find(string typeName, Action<IQueryBuilder> filter = null, Guid? inContext = null, bool includeFrozen = true) =>
            Find(new[] { typeName }, filter, inContext, includeFrozen);

        /// <summary>Поиск объектов сразу нескольких типов.</summary>
        public static IList<RObject> Find(IEnumerable<string> typeNames, Action<IQueryBuilder> filter = null, Guid? inContext = null, bool includeFrozen = true)
        {
            var typeIds = (typeNames ?? Enumerable.Empty<string>()).Select(n => RequireType(n).Id).ToArray();
            return Query(b =>
            {
                if (typeIds.Length == 1)
                    b.Must(ObjectFields.TypeId.Be(typeIds[0]));
                else if (typeIds.Length > 1)
                    b.Must(ObjectFields.TypeId.BeAnyOf(typeIds));
                filter?.Invoke(b);
            }, inContext, includeFrozen);
        }

        /// <summary>Поиск по строковому атрибуту: <c>Report.FindByAttr("project_contract", "doc_number", "123")</c>.</summary>
        public static IList<RObject> FindByAttr(string typeName, string attributeName, string value, Guid? inContext = null) =>
            Find(typeName, b => b.Must(AttributeFields.String(attributeName).Be(value)), inContext);

        /// <summary>Поиск по состоянию (UserState) - по имени или заголовку состояния.</summary>
        public static IList<RObject> FindByState(string typeName, string stateName, string stateAttribute = "state", Guid? inContext = null)
        {
            var state = StateByName(stateName) ?? throw new ReportException($"В базе нет состояния '{stateName}'");
            return Find(typeName, b => b.Must(AttributeFields.State(stateAttribute).Be(state.Id)), inContext);
        }

        /// <summary>Поиск по атрибуту-оргединице (исполнитель, ответственный...): любое из указанных Id должностей.</summary>
        public static IList<RObject> FindByOrgUnit(string typeName, string attributeName, IEnumerable<int> positionIds, Guid? inContext = null) =>
            Find(typeName, b => b.Must(AttributeFields.OrgUnit(attributeName).BeAnyOf(positionIds.ToArray())), inContext);

        /// <summary>Произвольный поиск: условия целиком задаёт вызывающий.</summary>
        public static IList<RObject> Query(Action<IQueryBuilder> build, Guid? inContext = null, bool includeFrozen = true)
        {
            var builder = includeFrozen
                ? QueryBuilder.CreateObjectQueryBuilder(new[] { ObjectState.Alive, ObjectState.Frozen })
                : QueryBuilder.CreateObjectQueryBuilder();
            build?.Invoke(builder);
            return Context.GetObjects(builder, inContext);
        }

        // ------------------------------------------------------------------
        // Параметры отчёта
        // ------------------------------------------------------------------

        /// <summary>Значение параметра отчёта. <c>var from = Report.Param&lt;DateTime&gt;(this, "dateFrom");</c></summary>
        public static T Param<T>(object report, string name) => ReportParams.Get<T>(report, name);

        /// <summary>Значение параметра; если не заполнено - ReportException с текстом <paramref name="message"/>.</summary>
        public static T RequireParam<T>(object report, string name, string message) => ReportParams.Require<T>(report, name, message);

        // ------------------------------------------------------------------

        private static Cache GetCache()
        {
            lock (Sync)
                return _cache ?? (_cache = new Cache(Context));
        }

        private sealed class Cache
        {
            public Cache(ReportContext context)
            {
                Units = context.GetOrganisationUnits().GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());

                Parents = new Dictionary<int, int>();
                foreach (var unit in Units.Values)
                {
                    foreach (var child in unit.Children ?? Enumerable.Empty<ROrganisationUnit>())
                        Parents[child.Id] = unit.Id;
                }

                States = context.GetUserStates().GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
                TypesByName = context.GetTypes().GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
            }

            public Dictionary<int, ROrganisationUnit> Units { get; }
            public Dictionary<int, int> Parents { get; }
            public Dictionary<Guid, INUserState> States { get; }
            public Dictionary<string, RType> TypesByName { get; }
        }
    }
}
