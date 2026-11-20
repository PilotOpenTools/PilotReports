using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ascon.Pilot.Pilot.Reports;
using Reports;
using Reports.Infrastructure;
using Reports.Stats;
using Xunit;

namespace Reports.Tests
{
    public class StatsTests
    {
        [Fact]
        public void Json_is_snake_case_and_escaped()
        {
            var e = new ReportRunEvent
            {
                RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                StartedUtc = new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc),
                Report = "Отчёт \"тест\"\n",
                Status = "success",
                DurationMs = 42,
                Pages = 3,
            };

            var json = e.ToJson();

            Assert.StartsWith("{\"run_id\":\"11111111-2222-3333-4444-555555555555\",\"ts\":\"2026-09-26T10:00:00.000Z\"", json);
            Assert.Contains("\"report\":\"Отчёт \\\"тест\\\"\\n\"", json);
            Assert.Contains("\"duration_ms\":42", json);
            Assert.Contains("\"pages\":3", json);
            Assert.DoesNotContain("error_message", json);
            Assert.EndsWith("}", json);
            Assert.DoesNotContain(",}", json);
        }

        [Fact]
        public void Config_parses_and_keeps_defaults()
        {
            var config = ModuleConfig.Parse("{\"statsUrl\":\"http://x:5310/api/report-events\"}");
            Assert.True(config.StatsEnabled);
            Assert.Equal("http://x:5310/api/report-events", config.StatsUrl);

            Assert.False(ModuleConfig.Parse("{\"statsEnabled\":false}").StatsEnabled);
        }

        [Theory]
        [InlineData("http://10.0.0.3:5310/api/module-events", "http://10.0.0.3:5310/api/report-events")]
        [InlineData("http://10.0.0.3:5310/API/Module-Events/", "http://10.0.0.3:5310/api/report-events")]
        [InlineData("https://stats.example.com", "https://stats.example.com/api/report-events")]
        [InlineData("https://example.com/stats/api/module-events", "https://example.com/stats/api/report-events")]
        [InlineData("не адрес", null)]
        [InlineData("ftp://x/api/module-events", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Report_url_is_derived_from_common_stats_url(string common, string expected)
        {
            Assert.Equal(expected, ModuleConfig.ReportEventsUrl(common));
        }

        [Fact]
        public void Common_settings_override_file_stats_until_cleared()
        {
            var config = ModuleConfig.Parse("{\"statsUrl\":\"http://file:5310/api/report-events\",\"apiKey\":\"file-key\",\"reportsGitLabGroupUrl\":\"http://gl/reports\"}");

            // Лишние поля раздела (окно загрузки и т.д.) модулю отчётов не мешают.
            config.ApplyCommon("{\"statsEnabled\":true,\"statsUrl\":\"http://common:5310/api/module-events\",\"apiKey\":\"common-key\",\"flushSeconds\":15,\"ignoredModules\":[\"x\"]}");
            Assert.Equal("http://common:5310/api/report-events", config.StatsUrl);
            Assert.Equal("common-key", config.ApiKey);
            Assert.Equal("http://gl/reports", config.ReportsGitLabGroupUrl);

            config.ApplyCommon("{\"statsEnabled\":false,\"statsUrl\":\"http://common:5310/api/module-events\"}");
            Assert.False(config.StatsEnabled);

            config.ApplyCommon("");
            Assert.True(config.StatsEnabled);
            Assert.Equal("http://file:5310/api/report-events", config.StatsUrl);
            Assert.Equal("file-key", config.ApiKey);
        }

        [Fact]
        public void Common_settings_keep_file_address_when_their_address_is_unusable()
        {
            var config = ModuleConfig.Parse("{\"statsUrl\":\"http://file:5310/api/report-events\",\"apiKey\":\"file-key\"}");

            config.ApplyCommon("{\"statsUrl\":\"не адрес\",\"apiKey\":\"common-key\"}");
            Assert.Equal("http://file:5310/api/report-events", config.StatsUrl);
            Assert.Equal("file-key", config.ApiKey);

            config.ApplyCommon("{\"statsUrl\":\"http://common:5310/api/module-events\"}");
            config.ApplyCommon("это не json");
            Assert.Equal("http://common:5310/api/report-events", config.StatsUrl);
        }

        [Fact]
        public void Classify_distinguishes_validation_cancel_and_error()
        {
            Assert.Equal("validation", ReportTracker.Classify(new TargetInvocationException(new ReportException("Выберите проект"))));
            Assert.Equal("cancelled", ReportTracker.Classify(new AggregateException(new OperationCanceledException())));
            Assert.Equal("cancelled", ReportTracker.Classify(new LongRunningOperationCancelledException()));
            Assert.Equal("error", ReportTracker.Classify(new NullReferenceException()));
        }

        /// <summary>
        /// Полный цикл: Begin / события «PrintingSystem» / HTTP POST сразу после построения на локальный приёмник.
        /// Отчёт и PrintingSystem - фейки с теми же именами членов, что у DevExpress (трекер работает через рефлексию).
        /// </summary>
        [Fact]
        public void Tracks_runs_and_posts_them_to_collector()
        {
            var port = FreePort();
            var received = new List<string>();
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                var serverThread = new Thread(() => Serve(listener, received)) { IsBackground = true };
                serverThread.Start();

                ReportStats.Start(
                    new ModuleConfig { StatsEnabled = true, StatsUrl = $"http://localhost:{port}/api/report-events" },
                    new ClientEnvironment { Login = "tester", Server = "https://pilot-stage.example.com/db" });

                // 1. Успешное построение
                var ok = new FakeReport();
                var okRun = Report.Begin(ok, "Реестр НОПРИЗ");
                ok.PrintingSystem.RaiseAfterBuildPages(pages: 5);

                // 2. Отчёт остановлен проверкой (ReportException)
                var invalid = new FakeReport();
                Report.Begin(invalid, "Контроль подписания");
                invalid.PrintingSystem.RaiseException(new ReportException("Выберите проект"));

                // 3. Ошибка в скрипте
                var broken = new FakeReport();
                Report.Begin(broken, "Сломанный");
                broken.PrintingSystem.RaiseException(new NullReferenceException("oops"));

                // 4. Окно закрыли до окончания построения
                var closed = new FakeReport();
                Report.Begin(closed, "Долгий");
                closed.PrintingSystem.Dispose();

                Assert.True(ReportStats.WaitPending(TimeSpan.FromSeconds(15)), "отправка не завершилась");
                listener.Stop();

                // Каждое построение - отдельный запрос сразу после окончания, без накопления.
                Assert.Equal(4, received.Count);
                foreach (var request in received)
                {
                    var parts = request.Split(new[] { '\n' }, 2);
                    Assert.True(DateTime.TryParse(parts[0].Substring("X-Client-Time=".Length), out _), "нет заголовка X-Client-Time");
                    Assert.StartsWith("[", parts[1]);
                    Assert.Equal(1, CountOf(parts[1], "\"run_id\""));
                }
                var body = string.Join("\n", received);
                Assert.Contains($"\"run_id\":\"{okRun}\"", body);
                Assert.Contains("\"report\":\"Реестр НОПРИЗ\",\"status\":\"success\"", body);
                Assert.Contains("\"pages\":5", body);
                Assert.Contains("\"report\":\"Контроль подписания\",\"status\":\"validation\"", body);
                Assert.Contains("\"error_message\":\"Выберите проект\"", body);
                Assert.Contains("\"report\":\"Сломанный\",\"status\":\"error\"", body);
                Assert.Contains("\"error_type\":\"System.NullReferenceException\"", body);
                Assert.Contains("\"report\":\"Долгий\",\"status\":\"cancelled\"", body);
                Assert.Contains("\"login\":\"tester\"", body);
                Assert.Equal(4, CountOf(body, "\"run_id\""));
            }
        }

        /// <summary>
        /// Интеграция с настоящим сервисом report-metrics-collector. Запускается, только если задан адрес:
        /// set REPORTS_IT_URL=http://localhost:5310/api/report-events (и REPORTS_IT_KEY при необходимости).
        /// </summary>
        [IntegrationFact("REPORTS_IT_URL")]
        public async Task Posts_to_real_collector_when_configured()
        {
            var url = Environment.GetEnvironmentVariable("REPORTS_IT_URL");

            ReportStats.Start(
                new ModuleConfig { StatsEnabled = true, StatsUrl = url, ApiKey = Environment.GetEnvironmentVariable("REPORTS_IT_KEY") },
                new ClientEnvironment { Login = "it-test", Person = "Интеграционный Тест", Server = "https://pilot-stage.example.com/db", Machine = Environment.MachineName });

            var report = new FakeReport();
            var runId = Report.Begin(report, "Интеграционный тест Reports");
            report.PrintingSystem.RaiseAfterBuildPages(pages: 2);
            Assert.True(ReportStats.WaitPending(TimeSpan.FromSeconds(15)), "отправка не завершилась");

            var summaryUrl = url.Replace("/api/report-events", "/api/report-events/summary?hours=1");
            using (var http = new System.Net.Http.HttpClient())
            {
                var summary = await http.GetStringAsync(summaryUrl);
                Assert.Contains("Интеграционный тест Reports", summary);
            }
        }

        private static void Serve(HttpListener listener, List<string> received)
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = listener.GetContext();
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        lock (received) received.Add("X-Client-Time=" + ctx.Request.Headers["X-Client-Time"] + "\n" + reader.ReadToEnd());
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static int CountOf(string text, string part)
        {
            var count = 0;
            for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    // ---- фейки DevExpress: те же имена членов, что у XtraReport/PrintingSystemBase ----

    public class FakeReport
    {
        public FakePrintingSystem PrintingSystem { get; } = new FakePrintingSystem();
        public string DisplayName => "";
    }

    public delegate void FakeExceptionEventHandler(object sender, FakeExceptionEventArgs e);

    public class FakeExceptionEventArgs : EventArgs
    {
        public FakeExceptionEventArgs(Exception exception) => Exception = exception;
        public Exception Exception { get; }
    }

    public class FakePrintingSystem : Component
    {
        public event EventHandler AfterBuildPages;
        public event FakeExceptionEventHandler CreateDocumentException;

        public List<object> Pages { get; } = new List<object>();

        public void RaiseAfterBuildPages(int pages)
        {
            Pages.AddRange(Enumerable.Range(0, pages).Select(i => new object()));
            AfterBuildPages?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseException(Exception ex) => CreateDocumentException?.Invoke(this, new FakeExceptionEventArgs(ex));
    }

    public class LongRunningOperationCancelledException : Exception
    {
    }
}
