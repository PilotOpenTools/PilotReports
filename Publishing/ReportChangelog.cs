using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Reports.Publishing
{
    /// <summary>
    /// CHANGELOG.md репозитория отчёта - тот же формат, что пишет веб-админка (Automations/Changelog.cs):
    /// «## [Не выпущено]» - отправки в dev, ниже разделы «## [2026-09-27]» - переносы в main (прод), новые сверху.
    /// Здесь только дописывание: пункт в «Не выпущено» при отправке в dev, раздел с датой при отправке сразу в main.
    /// </summary>
    internal static class ReportChangelog
    {
        public const string FileName = "CHANGELOG.md";
        private const string UnreleasedHeading = "## [Не выпущено]";
        private static readonly Regex ListMarker = new Regex(@"^[-*•]\s+");

        public static string DefaultPreamble(string name) =>
            $"# Журнал изменений: {name}\n\n" +
            "Новые записи сверху. «Не выпущено» - изменения в ветке `dev` (тестовый сервер), которые ещё не перенесены на прод. " +
            "Остальные разделы - переносы в `main` (продовый сервер).";

        /// <summary>Строки сообщения / пункты: строка = пункт, маркеры списка убираются.</summary>
        public static List<string> SplitNotes(string text) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(l => ListMarker.Replace(l.Trim(), ""))
                .Where(l => l.Length > 0)
                .ToList();

        /// <summary>Отправка в dev: пункты в начало «Не выпущено».</summary>
        public static string AddUnreleased(string existing, string reportName, IEnumerable<string> notes, DateTime date)
        {
            Split(existing, reportName, out var preamble, out var unreleased, out var released);
            unreleased.InsertRange(0, notes.Select(n => $"- {n} _({date:dd.MM.yyyy})_"));
            return Render(preamble, unreleased, released);
        }

        /// <summary>Отправка сразу в main: новый раздел с датой сверху, «Не выпущено» остаётся как было.</summary>
        public static string AddRelease(string existing, string reportName, IEnumerable<string> notes, DateTime date)
        {
            Split(existing, reportName, out var preamble, out var unreleased, out var released);
            var section = $"## [{date:yyyy-MM-dd}]\n\n" + string.Join("\n", notes.Select(n => "- " + n));
            released = released.Length > 0 ? section + "\n\n" + released : section;
            return Render(preamble, unreleased, released);
        }

        private static void Split(string text, string name, out string preamble, out List<string> unreleased, out string released)
        {
            var lines = (text ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var i = 0;
            var head = new StringBuilder();
            for (; i < lines.Length && !lines[i].StartsWith("## "); i++)
                head.Append(lines[i]).Append('\n');
            preamble = head.ToString().Trim().Length > 0 ? head.ToString().Trim() : DefaultPreamble(name);

            unreleased = new List<string>();
            var rest = new StringBuilder();
            while (i < lines.Length)
            {
                var heading = lines[i].Substring(3).Trim().Trim('[', ']', ' ').ToLowerInvariant();
                var isUnreleased = heading == "не выпущено" || heading == "unreleased";
                if (!isUnreleased)
                    rest.Append(lines[i]).Append('\n');
                for (i++; i < lines.Length && !lines[i].StartsWith("## "); i++)
                {
                    if (!isUnreleased)
                        rest.Append(lines[i]).Append('\n');
                    else if (lines[i].Trim().Length > 0)
                        unreleased.Add(lines[i].TrimEnd());
                }
            }
            released = rest.ToString().Trim();
        }

        private static string Render(string preamble, List<string> unreleased, string released)
        {
            var sb = new StringBuilder(preamble.TrimEnd()).Append("\n\n");
            if (unreleased.Count > 0)
                sb.Append(UnreleasedHeading).Append("\n\n").Append(string.Join("\n", unreleased)).Append("\n\n");
            if (released.Length > 0)
                sb.Append(released);
            return sb.ToString().TrimEnd() + "\n";
        }
    }
}
