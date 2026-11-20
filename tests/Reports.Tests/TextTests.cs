using System.Collections.Generic;
using System.Linq;
using Reports;
using Xunit;

namespace Reports.Tests
{
    public class TextTests
    {
        [Theory]
        [InlineData("Иванов Иван Иванович", "И.И. Иванов")]
        [InlineData("Иванов Иван", "И. Иванов")]
        [InlineData("  Петров  пётр  сергеевич ", "П.С. Петров")]
        [InlineData("Администратор", "Администратор")]
        [InlineData(null, "")]
        public void Fio(string input, string expected) => Assert.Equal(expected, Text.Fio(input));

        [Theory]
        [InlineData("Иванов Иван Иванович", "Иванов И.И.")]
        [InlineData("Иванов Иван", "Иванов И.")]
        [InlineData("", "")]
        public void ShortName(string input, string expected) => Assert.Equal(expected, Text.ShortName(input));

        [Fact]
        public void CleanTitle_removes_all_quotes() =>
            Assert.Equal("АО Мосинжпроект", Text.CleanTitle(" АО «Мосинжпроект\" "));

        [Fact]
        public void CompareCodes_sorts_by_numbers()
        {
            var codes = new List<string> { "1.10", "1.2", "2", "1.2.1", "", "1.1" };
            var sorted = codes.OrderBy(c => c, Text.CodeComparer).ToList();
            Assert.Equal(new[] { "", "1.1", "1.2", "1.2.1", "1.10", "2" }, sorted);
        }

        [Fact]
        public void JoinNonEmpty_skips_blanks() =>
            Assert.Equal("a, b", Text.JoinNonEmpty(", ", "a", " ", null, "b"));
    }
}

namespace Reports.Tests
{
    public class SoftSnippetTests
    {
        /// <summary>
        /// «Мягкая» строка из tools/add_report_stats.py (SNIPPET) - должна находить модуль без using и звать Report.Begin.
        /// При изменении SNIPPET обновите и этот тест.
        /// </summary>
        private sealed class ScriptLikeReport : FakeReport
        {
            public void PilotReport_DataSourceDemanded(object sender, System.EventArgs e)
            {
                // Reports:stats - статистика построений (Grafana). Без модуля Reports строка ничего не делает.
                try { foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType("Reports.Report"); if (t != null) { t.GetMethod("Begin").Invoke(null, new object[] { this, "Реестр НОПРИЗ", null }); break; } } } catch { }
            }
        }

        [Xunit.Fact]
        public void Soft_snippet_calls_module()
        {
            var report = new ScriptLikeReport();
            Xunit.Assert.False(Reports.Stats.ReportTracker.IsTracked(report.PrintingSystem));
            report.PilotReport_DataSourceDemanded(null, System.EventArgs.Empty);
            Xunit.Assert.True(Reports.Stats.ReportTracker.IsTracked(report.PrintingSystem));
        }
    }
}
