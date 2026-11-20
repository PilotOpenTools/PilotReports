using System;
using System.Diagnostics;

namespace Reports.Infrastructure
{
    /// <summary>
    /// Открытие адреса в браузере. Адреса приходят из ответов GitLab, поэтому ShellExecute получает только http и https:
    /// file:, UNC и прочие схемы не запускаются.
    /// </summary>
    internal static class ExternalLink
    {
        /// <summary>Запуск адреса оболочкой (в тестах подменяется).</summary>
        internal static Action<string> Launcher = url => Process.Start(url);

        public static bool IsSafe(string url, out Uri uri) =>
            Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        /// <summary>Открывает адрес; для небезопасной схемы пишет в лог и возвращает false.</summary>
        public static bool Open(string url)
        {
            if (!IsSafe(url, out var uri))
            {
                ModuleLog.Warn("Адрес не открыт: допускаются только http и https - " + url);
                return false;
            }
            Launcher(uri.AbsoluteUri);
            return true;
        }
    }
}
