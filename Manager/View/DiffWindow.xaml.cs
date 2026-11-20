using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Reports.Manager
{
    /// <summary>
    /// Сравнение отчёта в Pilot с веткой GitLab: скрипт и разметка отдельно.
    /// «−» - есть только в Pilot, «+» - придёт из ветки при обновлении.
    /// </summary>
    internal partial class DiffWindow : Window
    {
        internal DiffWindow(string reportName, string branch, RepxText pilot, RepxText gitlab, string updateText)
        {
            InitializeComponent();
            Title = $"Сравнение: Pilot / {branch}";
            Header.Text = $"{reportName}: Pilot / {branch}";

            var script = LineDiff.Compute(pilot.Script, gitlab.Script);
            var markup = LineDiff.Compute(pilot.Markup, gitlab.Markup);
            Legend.Text = $"«−» - только в Pilot, «+» - только в {branch} (придёт в Pilot при обновлении). " +
                          $"Скрипт: {Summary(script)}; разметка: {Summary(markup)}.";

            ScriptTab.Header = $"Скрипт ({Summary(script)})";
            MarkupTab.Header = $"Разметка ({Summary(markup)})";
            ScriptList.ItemsSource = Rows(script);
            MarkupList.ItemsSource = Rows(markup);
            if (script.Identical && !markup.Identical)
                MarkupTab.IsSelected = true;

            UpdateButton.Content = updateText;
            UpdateButton.Visibility = string.IsNullOrEmpty(updateText) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnUpdate(object sender, RoutedEventArgs e) => DialogResult = true;

        private static string Summary(DiffResult diff) =>
            diff.Identical ? "без изменений" : $"+{diff.Added} −{diff.Removed}";

        private static List<DiffRow> Rows(DiffResult diff)
        {
            if (diff.Identical)
                return new List<DiffRow> { DiffRow.Header("Различий нет.") };
            if (diff.TooLarge)
                return new List<DiffRow> { DiffRow.Header($"Различий слишком много для построчного сравнения: +{diff.Added} −{diff.Removed} строк.") };

            var rows = new List<DiffRow>();
            foreach (var hunk in diff.Hunks)
            {
                rows.Add(DiffRow.Header($"@@ строка {hunk.OldStart} в Pilot, {hunk.NewStart} в ветке @@"));
                rows.AddRange(hunk.Lines.Select(l => new DiffRow
                {
                    Kind = l.Kind.ToString(),
                    OldNo = l.OldNo?.ToString(),
                    NewNo = l.NewNo?.ToString(),
                    Sign = l.Kind == DiffKind.Added ? "+" : l.Kind == DiffKind.Removed ? "−" : "",
                    Text = l.Text.Replace("\t", "    "),
                }));
            }
            return rows;
        }

        internal sealed class DiffRow
        {
            public string Kind { get; set; }
            public string OldNo { get; set; }
            public string NewNo { get; set; }
            public string Sign { get; set; }
            public string Text { get; set; }

            public static DiffRow Header(string text) => new DiffRow { Kind = "Header", Text = text };
        }
    }
}
