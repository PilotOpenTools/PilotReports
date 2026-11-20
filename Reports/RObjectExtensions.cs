using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Pilot.Reports;

namespace Reports
{
    /// <summary>
    /// Самые частые операции с RObject в отчётах - все методы безопасны к null
    /// (объект null, атрибута нет, значение null) и не бросают исключений.
    /// <code>
    /// doc.Str("name")                  // строка или ""
    /// doc.DateStr("deadlineDate")      // "31.12.2026" или ""
    /// doc.People("executor")           // "Иванов Иван Иванович, Петров Пётр Петрович"
    /// doc.StateTitle()                 // заголовок состояния из атрибута "state"
    /// doc.Project()                    // ближайшая папка проекта (тип 0_project)
    /// doc.Related(RelationType.TaskAttachments)
    /// </code>
    /// </summary>
    public static class RObjectExtensions
    {
        public const string ProjectType = "0_project";
        public const string RootType = "Root_object_type";
        private const int MaxDepth = 100;

        // ------------------------------------------------------------------
        // Атрибуты
        // ------------------------------------------------------------------

        /// <summary>Сырое значение атрибута или null.</summary>
        public static object Attr(this RObject obj, string name) => AttributeValues.Attr(obj?.Attributes, name);

        /// <summary>Значение атрибута нужного типа или default (если атрибута нет или тип другой).</summary>
        public static T Attr<T>(this RObject obj, string name) => AttributeValues.Attr<T>(obj?.Attributes, name);

        /// <summary>Есть ли у объекта непустой атрибут.</summary>
        public static bool Has(this RObject obj, string name) => AttributeValues.Has(obj?.Attributes, name);

        /// <summary>Строковое значение атрибута или "" (для GetStringAttributeValue/GetStringAttributeByObject).</summary>
        public static string Str(this RObject obj, string name) => obj.StrOrNull(name) ?? string.Empty;

        /// <summary>Строковое значение атрибута или null.</summary>
        public static string StrOrNull(this RObject obj, string name) => AttributeValues.Str(obj.Attr(name));

        /// <summary>Дата из атрибута или null. UTC приводится к локальному времени.</summary>
        public static DateTime? Date(this RObject obj, string name) => AttributeValues.Date(obj.Attr(name));

        /// <summary>Дата строкой ("dd.MM.yyyy" по умолчанию) или "".</summary>
        public static string DateStr(this RObject obj, string name, string format = "dd.MM.yyyy") =>
            AttributeValues.DateStr(obj.Attr(name), format);

        /// <summary>Целое число из атрибута (Integer в Pilot - long) или null.</summary>
        public static long? Int(this RObject obj, string name) => AttributeValues.Int(obj.Attr(name));

        /// <summary>Дробное число из атрибута или null.</summary>
        public static double? Double(this RObject obj, string name) => AttributeValues.Double(obj.Attr(name));

        // ------------------------------------------------------------------
        // Оргструктура в атрибутах (исполнитель, ответственный, ГИП...)
        // ------------------------------------------------------------------

        /// <summary>Id оргединиц (должностей) из атрибута-оргединицы. Пустой массив, если не заполнено.</summary>
        public static int[] OrgUnitIds(this RObject obj, string name) => AttributeValues.OrgUnitIds(obj.Attr(name));

        /// <summary>Оргединицы из атрибута (удалённые пропускаются).</summary>
        public static IList<ROrganisationUnit> OrgUnits(this RObject obj, string name) =>
            obj.OrgUnitIds(name).Select(Report.OrgUnit).Where(u => u != null).ToList();

        /// <summary>Люди на должностях из атрибута (вакантные должности пропускаются).</summary>
        public static IList<RPerson> Persons(this RObject obj, string name) =>
            obj.OrgUnits(name)
                .Where(u => u.IsPosition && u.Person != null)
                .Select(u => u.Person)
                .ToList();

        /// <summary>
        /// ФИО людей из атрибута через разделитель - замена GetOrgUnitAttributeValue.
        /// <paramref name="shortNames"/> = true даёт "Иванов И.И.".
        /// </summary>
        public static string People(this RObject obj, string name, string separator = ", ", bool shortNames = false) =>
            string.Join(separator, obj.Persons(name)
                .Select(p => shortNames ? Text.ShortName(p.DisplayName) : p.DisplayName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct());

        /// <summary>Названия должностей из атрибута через разделитель.</summary>
        public static string Positions(this RObject obj, string name, string separator = ", ") =>
            string.Join(separator, obj.OrgUnits(name).Select(u => u.Title).Where(t => !string.IsNullOrWhiteSpace(t)));

        // ------------------------------------------------------------------
        // Состояния
        // ------------------------------------------------------------------

        /// <summary>Id состояния из атрибута-состояния или null.</summary>
        public static Guid? StateId(this RObject obj, string name = "state") => AttributeValues.StateId(obj.Attr(name));

        /// <summary>Состояние объекта или null.</summary>
        public static INUserState State(this RObject obj, string name = "state")
        {
            var id = obj.StateId(name);
            return id.HasValue ? Report.State(id.Value) : null;
        }

        /// <summary>Заголовок состояния ("В работе") или "" - замена StatusDocument_GetValue.</summary>
        public static string StateTitle(this RObject obj, string name = "state") => obj.State(name)?.Title ?? string.Empty;

        /// <summary>Системное имя состояния ("in_work") или "".</summary>
        public static string StateName(this RObject obj, string name = "state") => obj.State(name)?.Name ?? string.Empty;

        /// <summary>
        /// Последняя дата, когда объект был в состоянии <paramref name="stateNameOrTitle"/>, по истории версий
        /// (для заданий - дата перехода в «Выполнено» и т.п.). null, если не был.
        /// </summary>
        public static DateTime? LastDateInState(this RObject obj, string stateNameOrTitle, string stateAttribute = "state")
        {
            if (obj == null)
                return null;

            bool IsNeeded(RObject o) => o.StateName(stateAttribute) == stateNameOrTitle || o.StateTitle(stateAttribute) == stateNameOrTitle;

            DateTime? last = null;
            foreach (var item in obj.HistoryItems ?? Enumerable.Empty<RHistoryItem>())
            {
                var version = item?.Object;
                if (version != null && IsNeeded(version) && (last == null || item.Created > last))
                    last = item.Created;
            }

            if (last == null && IsNeeded(obj))
                last = obj.HistoryItems?.Where(h => h != null).Select(h => (DateTime?)h.Created).Max() ?? obj.CreatedUtc;

            return last?.Kind == DateTimeKind.Utc ? last.Value.ToLocalTime() : last;
        }

        // ------------------------------------------------------------------
        // Иерархия
        // ------------------------------------------------------------------

        /// <summary>Не удалён и не в корзине.</summary>
        public static bool IsAlive(this RObject obj) => obj != null && !obj.IsDeleted && !obj.IsInRecycleBin;

        /// <summary>Заморожен ли объект.</summary>
        public static bool IsFrozen(this RObject obj) => obj != null && obj.State == ObjectState.Frozen;

        /// <summary>Родители снизу вверх (без самого объекта и без корня). Обращение к недоступному родителю обрывает цепочку.</summary>
        public static IEnumerable<RObject> Ancestors(this RObject obj)
        {
            var current = obj;
            for (var i = 0; i < MaxDepth && current != null; i++)
            {
                RObject parent;
                try
                {
                    parent = current.Parent;
                }
                catch
                {
                    yield break; // нет прав на родителя
                }

                if (parent == null || parent.Id == current.Id || parent.Type?.Name == RootType)
                    yield break;
                yield return parent;
                current = parent;
            }
        }

        /// <summary>Ближайший родитель одного из типов или null - замена GetParentByType/GetParentByTypes.</summary>
        public static RObject ParentOfType(this RObject obj, params string[] typeNames) =>
            obj.Ancestors().FirstOrDefault(p => typeNames.Contains(p.Type?.Name));

        /// <summary>Ближайший родитель одного из типов (по Id типа) или null.</summary>
        public static RObject ParentOfType(this RObject obj, params int[] typeIds) =>
            obj.Ancestors().FirstOrDefault(p => p.Type != null && typeIds.Contains(p.Type.Id));

        /// <summary>Сам объект, если он нужного типа, иначе ближайший родитель этого типа.</summary>
        public static RObject SelfOrParentOfType(this RObject obj, params string[] typeNames) =>
            obj != null && typeNames.Contains(obj.Type?.Name) ? obj : obj.ParentOfType(typeNames);

        /// <summary>Папка проекта (тип 0_project), в которой лежит объект, или null - замена GetProject/GetMainProjectFolder.</summary>
        public static RObject Project(this RObject obj) => obj.SelfOrParentOfType(ProjectType);

        /// <summary>Лежит ли объект внутри папки с одним из названий (по атрибуту name или Title).</summary>
        public static bool IsUnderFolder(this RObject obj, params string[] folderNames) =>
            obj.Ancestors().Any(p => folderNames.Contains(p.Str("name")) || folderNames.Contains(p.Title));

        /// <summary>Объект из учебных проектов или шаблонов - такие обычно исключают из отчётов.</summary>
        public static bool IsTrainingProject(this RObject obj) => obj.IsUnderFolder("Учебные проекты", "Шаблоны проектов");

        /// <summary>Живые дочерние объекты, при необходимости - только указанных типов.</summary>
        public static IList<RObject> AliveChildren(this RObject obj, params string[] typeNames) =>
            (obj?.Children ?? Enumerable.Empty<RObject>())
                .Where(c => c.IsAlive() && (typeNames.Length == 0 || typeNames.Contains(c.Type?.Name)))
                .ToList();

        // ------------------------------------------------------------------
        // Связи
        // ------------------------------------------------------------------

        /// <summary>
        /// Связанные объекты. Связи и их цели бывают null - отфильтровывается здесь.
        /// Удалённые и объекты в корзине не возвращаются.
        /// </summary>
        public static IList<RObject> Related(this RObject obj, RelationType? type = null, params string[] targetTypeNames) =>
            (obj?.Relations ?? Enumerable.Empty<RRelation>())
                .Where(r => r != null && (type == null || r.Type == type))
                .Select(r => r.Target)
                .Where(t => t.IsAlive() && (targetTypeNames.Length == 0 || targetTypeNames.Contains(t.Type?.Name)))
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .ToList();

        /// <summary>Первый связанный объект указанного типа или null.</summary>
        public static RObject RelatedOne(this RObject obj, params string[] targetTypeNames) =>
            obj.Related(null, targetTypeNames).FirstOrDefault();

        // ------------------------------------------------------------------
        // Разное
        // ------------------------------------------------------------------

        /// <summary>Ссылка на объект для гиперссылки в отчёте (открывает объект в Pilot).</summary>
        public static string Link(this RObject obj)
        {
            if (obj == null)
                return string.Empty;
            try
            {
                return Report.Context.GetPilotUrl(obj.Id, null);
            }
            catch
            {
                return "piloturi://" + obj.Id;
            }
        }

        /// <summary>Имя объекта: атрибут name, иначе Title.</summary>
        public static string Name(this RObject obj) => obj.StrOrNull("name") ?? obj?.Title ?? string.Empty;
    }

    /// <summary>
    /// Преобразование значений атрибутов отдельно от RObject: логику можно проверять без объектов клиента Pilot,
    /// а внутреннее устройство RObject меняется от версии к версии клиента.
    /// </summary>
    internal static class AttributeValues
    {
        public static object Attr(IDictionary<string, object> attributes, string name)
        {
            if (attributes == null || name == null)
                return null;
            return attributes.TryGetValue(name, out var value) ? value : null;
        }

        public static T Attr<T>(IDictionary<string, object> attributes, string name) =>
            Attr(attributes, name) is T value ? value : default;

        public static bool Has(IDictionary<string, object> attributes, string name) => Attr(attributes, name) != null;

        public static string Str(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case DateTime d: return d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                case IFormattable f: return f.ToString(null, CultureInfo.GetCultureInfo("ru-RU"));
                default: return value.ToString();
            }
        }

        public static DateTime? Date(object value)
        {
            if (!(value is DateTime date))
                return null;
            return date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        }

        public static string DateStr(object value, string format) =>
            Date(value)?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

        public static long? Int(object value)
        {
            switch (value)
            {
                case null: return null;
                case long l: return l;
                case int i: return i;
                case double d: return (long)d;
                default: return long.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : (long?)null;
            }
        }

        public static double? Double(object value)
        {
            switch (value)
            {
                case null: return null;
                case double d: return d;
                case long l: return l;
                case int i: return i;
                case decimal m: return (double)m;
                default:
                    var s = value.ToString().Replace(',', '.');
                    return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : (double?)null;
            }
        }

        public static int[] OrgUnitIds(object value)
        {
            switch (value)
            {
                case int[] ids: return ids;
                case int id: return new[] { id };
                case IEnumerable items when !(items is string):
                    return items.Cast<object>().Where(x => x != null).Select(Convert.ToInt32).ToArray();
                default: return Array.Empty<int>();
            }
        }

        public static Guid? StateId(object value)
        {
            switch (value)
            {
                case Guid g: return g;
                case string s when Guid.TryParse(s, out var parsed): return parsed;
                default: return null;
            }
        }
    }
}
