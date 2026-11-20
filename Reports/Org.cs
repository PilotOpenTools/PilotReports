using System.Collections.Generic;
using System.Linq;
using Ascon.Pilot.Pilot.Reports;

namespace Reports
{
    /// <summary>
    /// Оргструктура: отдел сотрудника, все должности подразделения и т.п.
    /// Замена FiilDictOrg / OrganisationUnitService / GetPositions, которые копировались в отчёты.
    /// </summary>
    public static class Org
    {
        /// <summary>Подразделение, в котором находится должность (ближайший родитель - не должность).</summary>
        public static ROrganisationUnit DepartmentOf(int positionId)
        {
            var parent = Report.ParentUnit(positionId);
            while (parent != null && parent.IsPosition)
                parent = Report.ParentUnit(parent.Id);
            return parent;
        }

        /// <summary>Подразделение сотрудника по его основной должности (или первой из должностей).</summary>
        public static ROrganisationUnit DepartmentOf(RPerson person)
        {
            var position = person?.MainPosition ?? person?.Positions?.FirstOrDefault();
            return position == null ? null : DepartmentOf(position.Id);
        }

        /// <summary>Название подразделения сотрудника или "".</summary>
        public static string DepartmentTitle(RPerson person) => DepartmentOf(person)?.Title ?? string.Empty;

        /// <summary>
        /// Словарь «ФИО сотрудника / название отдела» - прямая замена FiilDictOrg.
        /// Учитываются только занятые должности.
        /// </summary>
        public static Dictionary<string, string> DepartmentByPersonName()
        {
            var result = new Dictionary<string, string>();
            foreach (var unit in Report.OrgUnits.Values.Where(u => u.IsPosition && u.Person != null && !u.IsDeleted))
            {
                var name = unit.Person.DisplayName;
                var department = DepartmentOf(unit.Id);
                if (!string.IsNullOrEmpty(name) && department != null && !result.ContainsKey(name))
                    result.Add(name, department.Title);
            }
            return result;
        }

        /// <summary>Id всех должностей внутри подразделения (рекурсивно), включая его самого - для поиска по исполнителю.</summary>
        public static IList<int> PositionIds(ROrganisationUnit unit)
        {
            var result = new List<int>();
            Collect(unit, result, 0);
            return result;
        }

        private static void Collect(ROrganisationUnit unit, List<int> result, int depth)
        {
            if (unit == null || depth > 50)
                return;
            result.Add(unit.Id);
            if (!unit.IsPosition)
            {
                foreach (var child in unit.Children ?? Enumerable.Empty<ROrganisationUnit>())
                    Collect(child, result, depth + 1);
            }
        }

        /// <summary>Сотрудники подразделения (рекурсивно), без повторов.</summary>
        public static IList<RPerson> PeopleIn(ROrganisationUnit unit) =>
            PositionIds(unit)
                .Select(Report.OrgUnit)
                .Where(u => u != null && u.IsPosition && u.Person != null)
                .Select(u => u.Person)
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .ToList();

        /// <summary>Оргединица по точному названию или null.</summary>
        public static ROrganisationUnit UnitByTitle(string title) =>
            Report.OrgUnits.Values.FirstOrDefault(u => !u.IsDeleted && u.Title == title);
    }
}
