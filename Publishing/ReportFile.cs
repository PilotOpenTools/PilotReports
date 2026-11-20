using System;

namespace Reports.Publishing
{
    /// <summary>Содержимое .repx при сохранении из дизайнера.</summary>
    internal static class ReportFile
    {
        private static readonly byte[] Closing = System.Text.Encoding.ASCII.GetBytes("</XtraReportsLayoutSerializer>");

        /// <summary>
        /// Дизайнер Pilot пишет новый отчёт поверх старого файла без обрезки: если отчёт стал короче, после закрывающего
        /// тега остаётся хвост старого файла, и такой XML не проходит проверку. Возвращает файл без хвоста.
        /// </summary>
        public static byte[] TrimTail(byte[] content)
        {
            if (content == null)
                return null;
            var end = FirstClosingEnd(content);
            if (end < 0 || end == content.Length)
                return content;
            var trimmed = new byte[end];
            Array.Copy(content, trimmed, end);
            return trimmed;
        }

        // Первое вхождение: всё, что после него, - остаток старого файла (в нём может быть и свой закрывающий тег).
        private static int FirstClosingEnd(byte[] content)
        {
            for (var i = 0; i <= content.Length - Closing.Length; i++)
            {
                var match = true;
                for (var j = 0; j < Closing.Length && match; j++)
                    match = content[i + j] == Closing[j];
                if (match)
                    return i + Closing.Length;
            }
            return -1;
        }
    }
}
