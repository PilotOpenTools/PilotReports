using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Reports.Publishing
{
    /// <summary>
    /// Сопоставление отчёта Pilot с проектом GitLab - те же правила, что в веб-админке (ProjectNames/ReportFiles):
    /// имя проекта = имя отчёта без учёта регистра, «ё/е» и символов, недопустимых в имени проекта GitLab.
    /// </summary>
    internal static class ReportNames
    {
        public const string Extension = ".repx";
        private static readonly char[] InvalidFileChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        public static string Key(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "")
                sb.Append(IsNameChar(c) ? c : ' ');
            var result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            while (result.Length > 0 && !(char.IsLetterOrDigit(result[0]) || result[0] == '_'))
                result = result.Substring(1).TrimStart();
            return result.ToLowerInvariant().Replace('ё', 'е');
        }

        public static string DefaultPath(string reportName) =>
            new string(reportName.Trim().Select(c => InvalidFileChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()) + Extension;

        /// <summary>Путь .repx отчёта в дереве ветки; null - файла нет (отправка создаст <see cref="DefaultPath"/>).</summary>
        public static string FindPath(IEnumerable<GitLabTreeItem> tree, string reportName)
        {
            var repx = (tree ?? Enumerable.Empty<GitLabTreeItem>())
                .Where(i => i.Type == "blob" && i.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Path)
                .ToList();
            if (repx.Count == 0)
                return null;

            var preferred = DefaultPath(reportName);
            var exact = repx.FirstOrDefault(p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            var key = Key(reportName);
            var byName = repx.Where(p => Key(Path.GetFileNameWithoutExtension(p)) == key).ToList();
            if (byName.Count == 1)
                return byName[0];

            return repx.Count == 1 ? repx[0] : null;
        }

        /// <summary>
        /// Допустимое имя проекта GitLab, максимально близкое к имени отчёта: запрещённые символы / пробел
        /// (как ProjectNames.Suggest в веб-админке). По <see cref="Key"/> совпадает с исходным именем.
        /// </summary>
        public static string Suggest(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "")
                sb.Append(IsNameChar(c) ? c : ' ');
            var result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            while (result.Length > 0 && !(char.IsLetterOrDigit(result[0]) || result[0] == '_'))
                result = result.Substring(1).TrimStart();
            while (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || result.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.LastIndexOf('.')).TrimEnd();
            if (result.Length == 0)
                result = "Report";
            return result.Length > 255 ? result.Substring(0, 255).TrimEnd() : result;
        }

        /// <summary>Ошибка в имени проекта GitLab; null - имя допустимо.</summary>
        public static string NameError(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Укажите имя репозитория.";
            if (name != name.Trim())
                return "Имя не должно начинаться или заканчиваться пробелом.";
            if (name.Length > 255)
                return "Имя длиннее 255 символов.";
            if (!(char.IsLetterOrDigit(name[0]) || name[0] == '_'))
                return "Имя должно начинаться с буквы, цифры или «_» (так требует GitLab).";
            var bad = name.Where(c => !IsNameChar(c)).Distinct().ToArray();
            if (bad.Length > 0)
                return $"Недопустимые для имени проекта GitLab символы: {string.Join(" ", bad.Select(c => $"«{c}»"))}. " +
                       "Можно: буквы, цифры, пробел, «_», «-», «.», «+».";
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
                return "Имя не может заканчиваться на .git или .atom.";
            return null;
        }

        /// <summary>Путь (slug) проекта: транслит, латиница в нижнем регистре, цифры, «-», «_», «.».</summary>
        public static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in (name ?? "").ToLowerInvariant())
                sb.Append(Translit.TryGetValue(c, out var t) ? t : c.ToString());
            var slug = Regex.Replace(sb.ToString(), "[^a-z0-9._-]+", "-");
            slug = Regex.Replace(slug, "-{2,}", "-").Trim('-', '.', '_');
            if (slug.EndsWith(".git") || slug.EndsWith(".atom"))
                slug = slug.Replace('.', '-');
            if (slug.Length > 100)
                slug = slug.Substring(0, 100).Trim('-', '.', '_');
            return slug.Length == 0 ? "report" : slug;
        }

        private static readonly Dictionary<char, string> Translit = new Dictionary<char, string>
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
            ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
            ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
            ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        };

        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ' || c == '+';
    }
}
