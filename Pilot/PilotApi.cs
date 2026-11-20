using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Application = System.Windows.Application;
using System.Windows.Threading;
using Ascon.Pilot.SDK;

namespace Reports
{
    /// <summary>
    /// Доступ к Pilot SDK из скрипта отчёта. В обычном отчёте есть только ReportContext,
    /// а здесь - сервисы клиентского модуля: IObjectsRepository, ISearchService, IObjectModifier и др.
    /// <code>
    /// var me = PilotApi.Repository.GetCurrentPerson();
    /// var doc = PilotApi.LoadObject(id);          // IDataObject со всеми данными SDK
    /// </code>
    /// Методы Load*/Search* синхронные: их можно вызывать и в DataSourceDemanded (UI-поток),
    /// и внутри LongRunning.Start (фоновый поток).
    /// </summary>
    public static class PilotApi
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        public static IObjectsRepository Repository { get; private set; }
        public static ISearchService Search { get; private set; }
        public static IObjectModifier Modifier { get; private set; }
        public static IFileProvider Files { get; private set; }
        public static IPilotDialogService Dialogs { get; private set; }
        public static ITabServiceProvider Tabs { get; private set; }
        public static IClientInfo ClientInfo { get; private set; }

        /// <summary>Модуль загружен клиентом и сервисы SDK доступны.</summary>
        public static bool IsReady => Repository != null;

        internal static void Init(
            IObjectsRepository repository,
            ISearchService search,
            IObjectModifier modifier,
            IFileProvider files,
            IPilotDialogService dialogs,
            ITabServiceProvider tabs,
            IClientInfo clientInfo)
        {
            Repository = repository;
            Search = search;
            Modifier = modifier;
            Files = files;
            Dialogs = dialogs;
            Tabs = tabs;
            ClientInfo = clientInfo;
        }

        /// <summary>Текущий пользователь.</summary>
        public static IPerson CurrentPerson => EnsureReady().GetCurrentPerson();

        /// <summary>Тип по системному имени, null если такого нет.</summary>
        public static IType GetType(string name) => EnsureReady().GetType(name);

        /// <summary>Загружает объект через SDK. null - объекта нет или не дождались.</summary>
        public static IDataObject LoadObject(Guid id, TimeSpan? timeout = null) =>
            LoadObjects(new[] { id }, timeout).FirstOrDefault();

        /// <summary>
        /// Загружает объекты через SDK и ждёт окончания загрузки. Несуществующие объекты пропускаются.
        /// Порядок результата совпадает с порядком <paramref name="ids"/>.
        /// </summary>
        public static IReadOnlyList<IDataObject> LoadObjects(IEnumerable<Guid> ids, TimeSpan? timeout = null)
        {
            var repository = EnsureReady();
            var wanted = ids?.Distinct().ToList() ?? throw new ArgumentNullException(nameof(ids));
            if (wanted.Count == 0)
                return Array.Empty<IDataObject>();

            var loaded = new Dictionary<Guid, IDataObject>();
            var finished = new HashSet<Guid>();

            Wait(done => repository.SubscribeObjects(wanted).Subscribe(new Observer<IDataObject>(
                    obj =>
                    {
                        if (obj.State != DataState.Loaded && obj.State != DataState.NoData && obj.State != DataState.NonExistent)
                            return;

                        lock (finished)
                        {
                            if (obj.State == DataState.Loaded)
                                loaded[obj.Id] = obj;
                            if (finished.Add(obj.Id) && finished.Count == wanted.Count)
                                done();
                        }
                    },
                    _ => done())),
                timeout ?? DefaultTimeout);

            lock (finished)
                return wanted.Where(loaded.ContainsKey).Select(id => loaded[id]).ToList();
        }

        /// <summary>
        /// Серверный поиск через SDK. Возвращает идентификаторы найденных объектов.
        /// <code>
        /// var ids = PilotApi.SearchIds(b => b.Must(ObjectFields.TypeId.Be(type.Id)));
        /// </code>
        /// Для отчётов обычно удобнее <see cref="Report.Find(string, Action{Ascon.Pilot.Common.Search.IQueryBuilder}, Guid?, bool)"/> -
        /// он сразу отдаёт RObject.
        /// </summary>
        public static IReadOnlyList<Guid> SearchIds(Action<IQueryBuilder> build, TimeSpan? timeout = null)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var search = Search ?? throw NotReady();

            var builder = search.GetObjectQueryBuilder();
            builder.MaxResults(int.MaxValue);
            build(builder);

            IReadOnlyList<Guid> result = Array.Empty<Guid>();
            Wait(done => search.Search(builder).Subscribe(new Observer<ISearchResult>(
                    r =>
                    {
                        // Сначала приходит локальный результат из кэша клиента, полный - Remote.
                        if (r.Kind != SearchResultKind.Remote)
                            return;
                        result = r.Result.ToList();
                        done();
                    },
                    _ => done(),
                    done)),
                timeout ?? DefaultTimeout);
            return result;
        }

        /// <summary>Серверный поиск через SDK с загрузкой найденных объектов.</summary>
        public static IReadOnlyList<IDataObject> SearchObjects(Action<IQueryBuilder> build, TimeSpan? timeout = null) =>
            LoadObjects(SearchIds(build, timeout), timeout);

        /// <summary>
        /// Ждёт завершения подписки SDK. Уведомления SDK приходят через диспетчер UI, поэтому
        /// на UI-потоке ждём во вложенном цикле сообщений (как ObjectLoaderSync в NugetPackage),
        /// а на фоновом - просто блокируемся.
        /// </summary>
        private static void Wait(Func<Action, IDisposable> subscribe, TimeSpan timeout)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && dispatcher.CheckAccess())
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(timeout, DispatcherPriority.Send, (s, e) => frame.Continue = false, dispatcher);
                using (subscribe(() => frame.Continue = false))
                {
                    timer.Start();
                    if (frame.Continue)
                        Dispatcher.PushFrame(frame);
                    timer.Stop();
                }
                return;
            }

            // Фоновый поток (например, внутри LongRunning.Start). Подписку создаём и снимаем на UI-потоке -
            // SDK рассчитан на работу из него; LongRunning в это время прокачивает очередь сообщений.
            var signal = new ManualResetEventSlim(false);
            IDisposable subscription = null;
            void Done() => signal.Set();
            try
            {
                if (dispatcher != null)
                    dispatcher.Invoke(() => subscription = subscribe(Done));
                else
                    subscription = subscribe(Done);

                if (!signal.Wait(timeout))
                    Infrastructure.ModuleLog.Warn($"Pilot: не дождались ответа SDK за {timeout.TotalSeconds:0} с");
            }
            finally
            {
                if (subscription != null)
                {
                    if (dispatcher != null)
                        dispatcher.BeginInvoke(new Action(subscription.Dispose));
                    else
                        subscription.Dispose();
                }
                // signal не освобождаем: поздний колбэк SDK после отписки не должен падать на Dispose.
            }
        }

        private sealed class Observer<T> : IObserver<T>
        {
            private readonly Action<T> _next;
            private readonly Action<Exception> _error;
            private readonly Action _completed;

            public Observer(Action<T> next, Action<Exception> error, Action completed = null)
            {
                _next = next;
                _error = error;
                _completed = completed;
            }

            public void OnNext(T value) => _next(value);
            public void OnError(Exception error) => _error(error);
            public void OnCompleted() => _completed?.Invoke();
        }

        private static IObjectsRepository EnsureReady() => Repository ?? throw NotReady();

        private static InvalidOperationException NotReady() => new InvalidOperationException(
            "Модуль Reports не загружен клиентом Pilot: сервисы SDK недоступны. Проверьте, что модуль установлен на сервере.");
    }
}
