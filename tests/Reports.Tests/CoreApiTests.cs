using System;
using System.Collections.Generic;
using Reports;
using Ascon.Pilot.Pilot.Reports;
using Xunit;

namespace Reports.Tests
{
    /// <summary>
    /// Логика расширений RObject, которыми пользуются скрипты отчётов: чтение атрибутов безопасно к null и любым типам значений.
    /// Проверяется на словарях и значениях атрибутов: RObject строится из объектов клиента Pilot, а его внутренности отличаются между версиями клиента.
    /// </summary>
    public class RObjectAttributeTests
    {
        private static Dictionary<string, object> Attrs(params (string Name, object Value)[] attributes)
        {
            var dictionary = new Dictionary<string, object>();
            foreach (var (name, value) in attributes)
                dictionary[name] = value;
            return dictionary;
        }

        [Fact]
        public void Attr_is_null_safe()
        {
            Assert.Null(AttributeValues.Attr(null, "x")); // у отсутствующего объекта словаря атрибутов нет
            Assert.Null(AttributeValues.Attr(Attrs(("a", 1)), "b"));
            Assert.Null(AttributeValues.Attr(Attrs(("a", 1)), null));
            Assert.False(AttributeValues.Has(Attrs(("a", null)), "a"));
            Assert.True(AttributeValues.Has(Attrs(("a", 0)), "a"));
            Assert.Equal(5, AttributeValues.Attr<int>(Attrs(("a", 5)), "a"));
            Assert.Equal(0, AttributeValues.Attr<int>(Attrs(("a", "текст")), "a")); // другой тип - default
        }

        // Отсутствующий атрибут приходит в преобразования как null (см. Attr_is_null_safe).
        [Fact]
        public void Str_formats_values_the_way_reports_expect()
        {
            Assert.Equal("", AttributeValues.Str(null) ?? string.Empty);
            Assert.Null(AttributeValues.Str(null));
            Assert.Equal("текст", AttributeValues.Str("текст"));
            Assert.Equal("31.12.2026", AttributeValues.Str(new DateTime(2026, 12, 31)));
            Assert.Equal("1,5", AttributeValues.Str(1.5)); // десятичная запятая, не зависит от культуры потока
            Assert.Equal("42", AttributeValues.Str(42L));
        }

        [Fact]
        public void Date_converts_utc_to_local_and_keeps_the_rest()
        {
            var local = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Local);
            var utc = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

            Assert.Equal(local, AttributeValues.Date(local));
            Assert.Equal(utc.ToLocalTime(), AttributeValues.Date(utc));
            Assert.Null(AttributeValues.Date("01.03.2026"));
            Assert.Null(AttributeValues.Date(null));
            Assert.Equal("", AttributeValues.DateStr(null, "dd.MM.yyyy"));
            Assert.Equal("2026-03-01", AttributeValues.DateStr(local, "yyyy-MM-dd"));
        }

        [Fact]
        public void Int_accepts_numbers_and_numeric_strings()
        {
            Assert.Equal(7L, AttributeValues.Int(7L));
            Assert.Equal(7L, AttributeValues.Int(7));
            Assert.Equal(3L, AttributeValues.Int(3.9));
            Assert.Equal(42L, AttributeValues.Int("42"));
            Assert.Null(AttributeValues.Int("много"));
            Assert.Null(AttributeValues.Int(null));
        }

        [Fact]
        public void Double_accepts_comma_and_dot()
        {
            Assert.Equal(1.5, AttributeValues.Double("1,5"));
            Assert.Equal(1.5, AttributeValues.Double("1.5"));
            Assert.Equal(2.0, AttributeValues.Double(2L));
            Assert.Equal(2.5, AttributeValues.Double(2.5m));
            Assert.Null(AttributeValues.Double("нет"));
            Assert.Null(AttributeValues.Double(null));
        }

        [Fact]
        public void OrgUnitIds_handles_single_collection_and_empty()
        {
            Assert.Equal(new[] { 4 }, AttributeValues.OrgUnitIds(4));
            Assert.Equal(new[] { 1, 2 }, AttributeValues.OrgUnitIds(new[] { 1, 2 }));
            Assert.Equal(new[] { 1, 3 }, AttributeValues.OrgUnitIds(new List<object> { 1L, null, 3L }));
            Assert.Empty(AttributeValues.OrgUnitIds("строка"));
            Assert.Empty(AttributeValues.OrgUnitIds(null));
        }

        [Fact]
        public void StateId_reads_guid_or_guid_string()
        {
            var id = Guid.NewGuid();
            Assert.Equal(id, AttributeValues.StateId(id));
            Assert.Equal(id, AttributeValues.StateId(id.ToString()));
            Assert.Null(AttributeValues.StateId("не guid"));
            Assert.Null(AttributeValues.StateId(null));
            Assert.Equal(id, AttributeValues.StateId(AttributeValues.Attr(Attrs(("custom", id)), "custom")));
        }

        // Name() без атрибута берёт RObject.Title, для этого нужен сам RObject; здесь проверена только часть про атрибут.
        [Fact]
        public void Name_prefers_attribute()
        {
            Assert.Equal("Отчёт", AttributeValues.Str(AttributeValues.Attr(Attrs(("name", "Отчёт")), "name")));
        }
    }

    /// <summary>Параметры отчёта DevExpress читаются через рефлексию: проверяем на объектах с теми же свойствами.</summary>
    public class ReportParamsTests
    {
        private class FakeParameter
        {
            public string Name { get; set; }
            public object Value { get; set; }
        }

        private class FakeParametersReport
        {
            public List<FakeParameter> Parameters { get; } = new List<FakeParameter>();
        }

        private static FakeParametersReport Report(params (string Name, object Value)[] parameters)
        {
            var report = new FakeParametersReport();
            foreach (var (name, value) in parameters)
                report.Parameters.Add(new FakeParameter { Name = name, Value = value });
            return report;
        }

        [Fact]
        public void Get_returns_typed_value_or_default()
        {
            var report = Report(("n", 5), ("s", "7"), ("bad", "семь"), ("empty", ""), ("nothing", null));

            Assert.Equal(5, ReportParams.Get<int>(report, "n"));
            Assert.Equal(7, ReportParams.Get<int>(report, "s"));      // приведение строки к числу
            Assert.Equal(0, ReportParams.Get<int>(report, "bad"));    // не число - default, без исключения
            Assert.Equal("", ReportParams.Get<string>(report, "empty"));
            Assert.Equal(0, ReportParams.Get<int>(report, "nothing"));
            Assert.Equal(0, ReportParams.Get<int>(report, "нет такого"));
            Assert.Equal(0, ReportParams.Get<int>(null, "n"));
        }

        [Fact]
        public void Get_converts_multiselect_arrays_and_drops_foreign_items()
        {
            var report = Report(("m", new object[] { "a", 1, "b" }));

            Assert.Equal(new[] { "a", "b" }, ReportParams.Get<string[]>(report, "m"));
        }

        [Fact]
        public void Require_throws_report_exception_for_empty_values()
        {
            var report = Report(("ok", "значение"), ("blank", "  "), ("array", new string[0]), ("guid", Guid.Empty), ("nothing", null));

            Assert.Equal("значение", ReportParams.Require<string>(report, "ok", "Укажите значение"));
            foreach (var name in new[] { "blank", "nothing", "missing" })
            {
                var ex = Assert.Throws<ReportException>(() => ReportParams.Require<string>(report, name, "Укажите значение"));
                Assert.Equal("Укажите значение", ex.Message);
            }
            Assert.Throws<ReportException>(() => ReportParams.Require<string[]>(report, "array", "Укажите список"));
            Assert.Throws<ReportException>(() => ReportParams.Require<Guid>(report, "guid", "Укажите проект"));
        }

        [Fact]
        public void SetValue_changes_existing_parameter_and_rejects_unknown()
        {
            var report = Report(("p", 1));

            ReportParams.SetValue(report, "p", 2);
            Assert.Equal(2, ReportParams.GetValue(report, "p"));
            var ex = Assert.Throws<ArgumentException>(() => ReportParams.SetValue(report, "q", 1));
            Assert.Contains("'q'", ex.Message);
        }

        [Fact]
        public void Item_pairs_value_and_caption()
        {
            var item = ReportParams.Item(5, "Пять");
            Assert.Equal(5, item.Key);
            Assert.Equal("Пять", item.Value);
        }
    }
}
