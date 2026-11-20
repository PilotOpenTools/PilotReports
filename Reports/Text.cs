using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reports
{
    /// <summary>Работа со строками, которая повторяется в отчётах: ФИО, очистка названий, сортировка шифров.</summary>
    public static class Text
    {
        private static readonly Regex FullName = new Regex(@"^\s*(\S+)\s+(\S+)(?:\s+(\S+))?", RegexOptions.Compiled);

        /// <summary>"Иванов Иван Иванович" / "И.И. Иванов" (для подписей). Непохожее на ФИО возвращается как есть.</summary>
        public static string Fio(string fullName)
        {
            var m = FullName.Match(fullName ?? string.Empty);
            if (!m.Success)
                return fullName ?? string.Empty;
            return Initials(m) + " " + m.Groups[1].Value;
        }

        /// <summary>"Иванов Иван Иванович" / "Иванов И.И." (для таблиц).</summary>
        public static string ShortName(string fullName)
        {
            var m = FullName.Match(fullName ?? string.Empty);
            if (!m.Success)
                return fullName ?? string.Empty;
            return m.Groups[1].Value + " " + Initials(m);
        }

        private static string Initials(Match m)
        {
            var result = char.ToUpper(m.Groups[2].Value[0]) + ".";
            if (m.Groups[3].Success)
                result += char.ToUpper(m.Groups[3].Value[0]) + ".";
            return result;
        }

        /// <summary>Убирает кавычки всех видов и лишние пробелы по краям.</summary>
        public static string CleanTitle(string title) =>
            (title ?? string.Empty)
                .Replace("\"", "").Replace("'", "").Replace("«", "").Replace("»", "").Replace("“", "").Replace("”", "")
                .Trim();

        /// <summary>Склеивает непустые значения через разделитель.</summary>
        public static string JoinNonEmpty(string separator, params string[] values) =>
            string.Join(separator, (values ?? Array.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)));

        /// <summary>Склеивает непустые значения через разделитель.</summary>
        public static string JoinNonEmpty(string separator, IEnumerable<string> values) =>
            JoinNonEmpty(separator, values?.ToArray());

        /// <summary>
        /// Сравнение шифров/номеров вида "1.2.10" по числам: "1.2" &lt; "1.10" (обычная сортировка строк даёт наоборот).
        /// <code>docs.Sort((a, b) =&gt; Text.CompareCodes(a.Code, b.Code));</code>
        /// </summary>
        public static int CompareCodes(string a, string b)
        {
            a = a?.Trim() ?? string.Empty;
            b = b?.Trim() ?? string.Empty;
            if (string.Equals(a, b, StringComparison.Ordinal))
                return 0;
            if (a.Length == 0)
                return -1;
            if (b.Length == 0)
                return 1;

            var partsA = a.Split('.', '-', ' ');
            var partsB = b.Split('.', '-', ' ');
            for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
            {
                int result;
                if (long.TryParse(partsA[i], out var na) && long.TryParse(partsB[i], out var nb))
                    result = na.CompareTo(nb);
                else
                    result = string.Compare(partsA[i], partsB[i], StringComparison.CurrentCultureIgnoreCase);

                if (result != 0)
                    return result;
            }
            return partsA.Length.CompareTo(partsB.Length);
        }

        /// <summary>Компаратор для OrderBy: <c>docs.OrderBy(d =&gt; d.Code, Text.CodeComparer)</c>.</summary>
        public static IComparer<string> CodeComparer { get; } = Comparer<string>.Create(CompareCodes);
    }
}
