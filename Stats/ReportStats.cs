using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reports.Infrastructure;

namespace Reports.Stats
{
    /// <summary>
    /// Отправка событий статистики в сервис report-metrics-collector.
    /// Каждое построение отправляется сразу после окончания, в фоне - отчёт сеть не ждёт.
    /// Очереди нет: если сервис недоступен, событие пишется в лог и теряется
    /// (отчёты строят редко, копить нечего).
    /// </summary>
    internal static class ReportStats
    {
        // Небольшая пауза перед отправкой: исключение DevExpress иногда приходит сразу после
        // AfterBuildPages, и трекер успевает поправить статус «success» на «error».
        private static readonly TimeSpan SendDelay = TimeSpan.FromSeconds(1);

        private static ModuleConfig _config;
        private static ClientEnvironment _env = new ClientEnvironment();
        private static HttpClient _http;
        private static int _pending;

        public static bool Enabled => _config != null && _config.StatsEnabled && !string.IsNullOrWhiteSpace(_config.StatsUrl);

        public static void Start(ModuleConfig config, ClientEnvironment env)
        {
            _config = config;
            _env = env ?? new ClientEnvironment();
            // Клиент создаётся всегда: общие настройки могут включить статистику уже после старта.
            _http = _http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <summary>Отправляет событие в фоне. Никогда не бросает исключений.</summary>
        public static void Enqueue(ReportRunEvent e)
        {
            if (!Enabled || e == null)
                return;

            e.Login = e.Login ?? _env.Login;
            e.Person = e.Person ?? _env.Person;
            e.Server = e.Server ?? _env.Server;
            e.ClientType = e.ClientType ?? _env.ClientType;
            e.ClientVersion = e.ClientVersion ?? _env.ClientVersion;
            e.Machine = e.Machine ?? _env.Machine;
            e.ModuleVersion = ClientEnvironment.ModuleVersion;

            Interlocked.Increment(ref _pending);
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SendDelay).ConfigureAwait(false);
                    await SendAsync(e).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            });
        }

        /// <summary>Для тестов: дождаться окончания всех отправок.</summary>
        internal static bool WaitPending(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _pending) > 0)
            {
                if (DateTime.UtcNow > until)
                    return false;
                Thread.Sleep(20);
            }
            return true;
        }

        private static async Task SendAsync(ReportRunEvent e)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _config.StatsUrl))
                {
                    // Адрес и ключ читаются при каждой отправке: общие настройки меняют их без перезапуска клиента.
                    var apiKey = _config.ApiKey;
                    if (!string.IsNullOrEmpty(apiKey))
                        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                    request.Content = new StringContent("[" + e.ToJson() + "]", Encoding.UTF8, "application/json");
                    // По этому времени сервис поправит ts, если часы на рабочем месте сбиты.
                    request.Headers.TryAddWithoutValidation("X-Client-Time", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            ModuleLog.Warn($"Статистика '{e.Report}' не принята: {(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                }
            }
            catch (Exception ex)
            {
                ModuleLog.Warn($"Статистика '{e.Report}' не отправлена ({_config.StatsUrl}): {ex.GetBaseException().Message}");
            }
        }
    }
}
