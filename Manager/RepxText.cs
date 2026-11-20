using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Reports.Manager
{
    /// <summary>
    /// Текст .repx для сравнения. Скрипт отчёта лежит в одном атрибуте ScriptsSource (с экранированными
    /// переводами строк), поэтому построчный diff всего файла бесполезен: скрипт вынимается отдельно,
    /// а в разметке атрибут заменяется заглушкой.
    /// </summary>
    internal sealed class RepxText
    {
        private const string ScriptAttribute = "ScriptsSource";

        private RepxText(string script, string markup, bool isXml)
        {
            Script = script;
            Markup = markup;
            IsXml = isXml;
        }

        public string Script { get; }
        public string Markup { get; }

        /// <summary>false - файл не разобрался как XML, в <see cref="Markup"/> весь текст как есть.</summary>
        public bool IsXml { get; }

        public static RepxText Parse(byte[] content)
        {
            var text = Decode(content);
            XDocument doc;
            try
            {
                doc = XDocument.Parse(text, LoadOptions.None);
            }
            catch (Exception)
            {
                return new RepxText("", text, false);
            }

            var script = "";
            var attribute = doc.Descendants().Select(e => e.Attribute(ScriptAttribute)).FirstOrDefault(a => a != null);
            if (attribute != null)
            {
                script = attribute.Value;
                attribute.Value = "(скрипт - на вкладке «Скрипт»)";
            }
            return new RepxText(script, doc.ToString(SaveOptions.None), true);
        }

        /// <summary>Проверка файла перед загрузкой в Pilot: null - похоже на отчёт DevExpress, иначе причина.</summary>
        public static string Validate(byte[] content)
        {
            if (content == null || content.Length == 0)
                return "Файл пустой.";
            try
            {
                var root = XDocument.Parse(Decode(content)).Root;
                if (root == null)
                    return "В файле нет XML-разметки.";
                var looksLikeReport = root.Name.LocalName.IndexOf("XtraReport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      root.Descendants().Take(3).Any(e => e.Name.LocalName.IndexOf("XtraReport", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                      root.Attributes().Any(a => a.Value.IndexOf("XtraReport", StringComparison.OrdinalIgnoreCase) >= 0);
                return looksLikeReport ? null : "Файл не похож на отчёт DevExpress (.repx): в разметке нет XtraReport.";
            }
            catch (Exception ex)
            {
                return "Файл не разбирается как XML: " + ex.Message;
            }
        }

        private static string Decode(byte[] content)
        {
            using (var reader = new StreamReader(new MemoryStream(content ?? new byte[0]), new UTF8Encoding(false), true))
                return reader.ReadToEnd();
        }
    }
}
