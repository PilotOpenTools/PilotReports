using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Reports.Infrastructure;

namespace Reports.Stats
{
    /// <summary>
    /// Следит за построением одного отчёта и превращает его в событие статистики.
    ///
    /// Как Pilot строит отчёт: DataSourceDemanded (UI-поток) / тело LongRunning.Start (фон) /
    /// PrintingSystem строит страницы. Итог узнаём по событиям PrintingSystem:
    ///   AfterBuildPages         - страницы построены, успех;
    ///   CreateDocumentException - исключение в скрипте/построении (ReportException = «проверка не пройдена»,
    ///                             LongRunningOperationCancelledException = окно закрыли);
    ///   Disposed                - отчёт закрыт, так и не достроившись, - отмена.
    ///
    /// С DevExpress работаем только через рефлексию: модуль не должен ломаться,
    /// если в клиенте Pilot обновится версия DevExpress.
    /// </summary>
    internal sealed class ReportTracker
    {
        private static readonly ConditionalWeakTable<object, ReportTracker> Trackers = new ConditionalWeakTable<object, ReportTracker>();

        private readonly object _sync = new object();
        private readonly object _printingSystem;
        private Run _current;

        private ReportTracker(object printingSystem)
        {
            _printingSystem = printingSystem;
            Subscribe(printingSystem, "AfterBuildPages", nameof(OnAfterBuildPages));
            Subscribe(printingSystem, "CreateDocumentException", nameof(OnCreateDocumentException));
            Subscribe(printingSystem, "Disposed", nameof(OnDisposed));
        }

        /// <summary>Начинает учёт нового построения отчёта. Возвращает id построения (run_id).</summary>
        public static Guid Begin(object report, string reportName, Guid? reportId)
        {
            var printingSystem = GetProperty(report, "PrintingSystem");
            if (printingSystem == null)
            {
                ModuleLog.Warn($"Report.Begin: у отчёта '{reportName}' нет PrintingSystem - статистика не собирается");
                return Guid.Empty;
            }

            var tracker = Trackers.GetValue(printingSystem, ps => new ReportTracker(ps));
            return tracker.StartRun(ResolveName(report, reportName), reportId);
        }

        /// <summary>Для тестов: следит ли трекер за этим PrintingSystem.</summary>
        internal static bool IsTracked(object printingSystem) => Trackers.TryGetValue(printingSystem, out _);

        private Guid StartRun(string name, Guid? reportId)
        {
            lock (_sync)
            {
                // Перестроение до окончания предыдущего (сменили параметры) - предыдущее считаем отменённым.
                if (_current != null && _current.Event == null)
                    Finish(ReportStatus.Cancelled, null);

                _current = new Run(name, reportId);
                return _current.Id;
            }
        }

        public void OnAfterBuildPages(object sender, EventArgs e)
        {
            lock (_sync)
            {
                if (_current == null || _current.Event != null)
                    return;
                Finish(ReportStatus.Success, null, GetPageCount());
            }
        }

        public void OnCreateDocumentException(object sender, EventArgs e)
        {
            var exception = GetProperty(e, "Exception") as Exception;
            lock (_sync)
            {
                if (_current == null)
                    return;

                var status = Classify(exception);
                if (_current.Event == null)
                {
                    Finish(status, exception);
                }
                else if (_current.Event.Status == ReportStatus.Success && status != ReportStatus.Success)
                {
                    // Исключение пришло уже после AfterBuildPages: событие ещё в очереди, поправим его.
                    Describe(_current.Event, status, exception);
                }
            }
        }

        public void OnDisposed(object sender, EventArgs e)
        {
            lock (_sync)
            {
                if (_current != null && _current.Event == null)
                    Finish(ReportStatus.Cancelled, null);
            }
        }

        private void Finish(string status, Exception exception, int? pages = null)
        {
            var run = _current;
            run.Stopwatch.Stop();
            var e = new ReportRunEvent
            {
                RunId = run.Id,
                StartedUtc = run.StartedUtc,
                Report = run.Name,
                ReportId = run.ReportId,
                DurationMs = run.Stopwatch.ElapsedMilliseconds,
                Pages = pages,
            };
            Describe(e, status, exception);
            run.Event = e;
            ReportStats.Enqueue(e);
        }

        private static void Describe(ReportRunEvent e, string status, Exception exception)
        {
            e.Status = status;
            if (exception == null)
                return;

            var root = Unwrap(exception);
            e.ErrorType = root.GetType().FullName;
            var message = root.Message ?? string.Empty;
            e.ErrorMessage = message.Length > 1000 ? message.Substring(0, 1000) : message;
        }

        internal static string Classify(Exception exception)
        {
            if (exception == null)
                return ReportStatus.Error;

            var root = Unwrap(exception);
            var typeName = root.GetType().Name;
            if (typeName == "ReportException")
                return ReportStatus.Validation;
            if (typeName == "LongRunningOperationCancelledException" || root is OperationCanceledException)
                return ReportStatus.Cancelled;
            return ReportStatus.Error;
        }

        internal static Exception Unwrap(Exception exception)
        {
            var current = exception;
            while (true)
            {
                if (current is TargetInvocationException && current.InnerException != null)
                    current = current.InnerException;
                else if (current is AggregateException agg && agg.InnerExceptions.Count == 1)
                    current = agg.InnerExceptions[0];
                else
                    return current;
            }
        }

        private int? GetPageCount()
        {
            try
            {
                var pages = GetProperty(_printingSystem, "Pages");
                return GetProperty(pages, "Count") as int?;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveName(object report, string reportName)
        {
            if (!string.IsNullOrWhiteSpace(reportName))
                return reportName.Trim();

            foreach (var property in new[] { "DisplayName", "Name" })
            {
                if (GetProperty(report, property) is string value && !string.IsNullOrWhiteSpace(value) && value != "PilotReport")
                    return value;
            }
            return "<без имени>";
        }

        private void Subscribe(object target, string eventName, string handlerName)
        {
            try
            {
                var info = target.GetType().GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public);
                if (info == null)
                {
                    ModuleLog.Warn($"ReportTracker: у {target.GetType().FullName} нет события {eventName}");
                    return;
                }

                // Обработчики имеют сигнатуру (object, EventArgs) - к конкретным делегатам DevExpress
                // они привязываются за счёт контравариантности параметров.
                var method = typeof(ReportTracker).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.Public);
                info.AddEventHandler(target, Delegate.CreateDelegate(info.EventHandlerType, this, method));
            }
            catch (Exception ex)
            {
                ModuleLog.Warn($"ReportTracker: не удалось подписаться на {eventName}", ex);
            }
        }

        private static object GetProperty(object target, string name)
        {
            if (target == null)
                return null;
            try
            {
                return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private sealed class Run
        {
            public Run(string name, Guid? reportId)
            {
                Name = name;
                ReportId = reportId;
            }

            public Guid Id { get; } = Guid.NewGuid();
            public string Name { get; }
            public Guid? ReportId { get; }
            public DateTime StartedUtc { get; } = DateTime.UtcNow;
            public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
            /// <summary>Не null - построение завершено и событие отправлено в очередь.</summary>
            public ReportRunEvent Event { get; set; }
        }
    }

    internal static class ReportStatus
    {
        public const string Success = "success";
        public const string Error = "error";
        public const string Validation = "validation";
        public const string Cancelled = "cancelled";
    }
}
