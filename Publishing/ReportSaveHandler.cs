using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows;
using Ascon.Pilot.SDK;
using Reports.Infrastructure;
using Reports.Manager;
using IDataObject = Ascon.Pilot.SDK.IDataObject;

namespace Reports.Publishing
{
    /// <summary>
    /// При сохранении отчёта (новая версия .repx) показывает окно отправки в GitLab.
    /// Перенесено из модуля 043SendReportToGitlab: ссылка GitLabLink в скрипте отчёта больше не нужна -
    /// репозиторий ищется по имени отчёта в группе отчётов (общие настройки «PilotOpenTools отчёты (GitLab)»
    /// или reportsGitLabGroupUrl в Reports.config.json).
    /// «Отменить сохранение» в окне отменяет изменение в Pilot.
    /// </summary>
    [Export(typeof(IObjectChangeProcessor))]
    public sealed class ReportSaveHandler : IObjectChangeProcessor
    {
        private readonly IFileProvider _fileProvider;
        private readonly IObjectsRepository _repository;

        [ImportingConstructor]
        public ReportSaveHandler(IFileProvider fileProvider, IObjectsRepository repository)
        {
            _fileProvider = fileProvider;
            _repository = repository;
        }

        /// <summary>Логин Pilot - токен GitLab хранится отдельно для каждого пользователя.</summary>
        private string PilotLogin()
        {
            try { return _repository.GetCurrentPerson()?.Login ?? Environment.UserName; }
            catch { return Environment.UserName; }
        }

        /// <summary>Окно не открываем, пока администратор не указал группу GitLab: иначе оно показывает только «группа не указана».</summary>
        internal static bool ShouldOfferPublish(ModuleConfig config) =>
            config.PublishOnSave && ReportPublishService.TryParseGroupUrl(config.ReportsGitLabGroupUrl, out _, out _);

        private static string _skipLogged;

        public bool ProcessChanges(IEnumerable<DataObjectChange> changes, IObjectModifier modifier)
        {
            var config = ModuleConfig.Current;
            var list = changes?.ToList() ?? new List<DataObjectChange>();
            if (!ShouldOfferPublish(config))
            {
                LogSkipped(config, list);
                return true;
            }

            foreach (var change in list)
            {
                try
                {
                    if (change?.Old == null || change.New == null)
                        continue;
                    if (!PilotReports.IsReport(change.New))
                        continue;
                    if (!IsNewSnapshot(change.Old, change.New))
                        continue;

                    // Версию создало окно PilotReport: из GitLab - отправлять нечего, с диска - причина уже введена.
                    var pending = PendingSaves.Take(change.New.Id);
                    if (pending != null && !pending.OfferPublish)
                        continue;

                    var file = NewRepx(change.Old, change.New);
                    if (file == null)
                        continue;

                    byte[] content;
                    using (var source = _fileProvider.OpenRead(file))
                    using (var buffer = new MemoryStream())
                    {
                        source.CopyTo(buffer);
                        content = buffer.ToArray();
                    }

                    var read = content.Length;
                    content = ReportFile.TrimTail(content);
                    var hadTail = content.Length != read;
                    if (hadTail)
                        ModuleLog.Warn($"Отчёт «{PilotReports.ReportName(change.New)}»: убран хвост старого файла, {read - content.Length} байт");

                    var viewModel = new PublishViewModel(PilotReports.ReportName(change.New), file.Name, content, config, PilotLogin(),
                        pending?.Message, FolderTitle(change.New));
                    if (new PublishWindow(viewModel).ShowDialog() != true)
                        return false;

                    // «Что изменилось» / причина версии в Pilot. Причину снапшоту, созданному дизайнером, SDK задать
                    // не даёт, поэтому сохранение дизайнера отменяется, а тот же .repx сразу записывается версией
                    // с причиной (CreateFileSnapshot(reason)). Версии модуля (pending) причину уже имеют.
                    var reason = viewModel.SnapshotReason;
                    if (hadTail && string.IsNullOrEmpty(reason))
                        reason = "Убран хвост старого файла";
                    if (pending == null && !string.IsNullOrEmpty(reason) && list.Count == 1 &&
                        string.IsNullOrWhiteSpace(change.New.ActualFileSnapshot?.Reason))
                    {
                        var reportId = change.New.Id;
                        var fileName = file.Name;
                        Application.Current.Dispatcher.BeginInvoke(new Action(() => SaveWithReason(reportId, fileName, content, reason)));
                        return false;
                    }
                    if (!string.IsNullOrEmpty(reason))
                        ModuleLog.Warn($"Причина версии не записана: pending={pending != null}, изменений={list.Count}, " +
                                       $"причина дизайнера=«{change.New.ActualFileSnapshot?.Reason}»");
                }
                catch (Exception ex)
                {
                    // Ошибка модуля не должна мешать сохранить отчёт.
                    ModuleLog.Error("Окно отправки отчёта в GitLab", ex);
                }
            }
            return true;
        }

        /// <summary>Почему окно не открылось - в лог, один раз на причину, и только когда сохранён отчёт.</summary>
        private static void LogSkipped(ModuleConfig config, List<DataObjectChange> changes)
        {
            try
            {
                if (!changes.Any(c => c?.Old != null && c.New != null && PilotReports.IsReport(c.New) && IsNewSnapshot(c.Old, c.New)))
                    return;
                var reason = SkipReason(config);
                if (reason == _skipLogged)
                    return;
                _skipLogged = reason;
                ModuleLog.Info("Отчёт сохранён без окна отправки в GitLab: " + reason);
            }
            catch
            {
                // Лог не должен мешать сохранению отчёта.
            }
        }

        internal static string SkipReason(ModuleConfig config) =>
            !config.PublishOnSave
                ? "окно при сохранении выключено (publishOnSave)"
                : $"не указаны сервер и группа GitLab - заполните общие настройки «{(global::Reports.Pilot.GitLabCommonSettings.SectionTitle)}» " +
                  $"или reportsGitLabGroupUrl в {ModuleConfig.FileName}";

        private static void SaveWithReason(Guid reportId, string fileName, byte[] content, string reason)
        {
            try
            {
                var report = PilotApi.LoadObject(reportId)
                             ?? throw new InvalidOperationException("отчёт не загрузился из Pilot");
                PilotReports.Update(report, content, reason, new PendingSave { OfferPublish = false }, fileName);
                ModuleLog.Info($"Отчёт «{PilotReports.ReportName(report)}» сохранён в Pilot с причиной: {reason}");
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Сохранение отчёта с причиной", ex);
                MessageBox.Show($"Отчёт не сохранён в Pilot: {ex.Message}\n\nСохраните его в дизайнере ещё раз.",
                    "Reports", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Имя папки отчёта в Pilot - подсказка, в какую подгруппу GitLab создать репозиторий.</summary>
        private string FolderTitle(IDataObject report)
        {
            try { return PilotApi.LoadObject(report.ParentId, TimeSpan.FromSeconds(5))?.DisplayName; }
            catch { return null; }
        }

        private static bool IsNewSnapshot(IDataObject oldObject, IDataObject newObject) =>
            (newObject.PreviousFileSnapshots?.Count ?? 0) > (oldObject.PreviousFileSnapshots?.Count ?? 0);

        private static IFile NewRepx(IDataObject oldObject, IDataObject newObject)
        {
            var files = newObject.ActualFileSnapshot?.Files?
                .Where(f => f.Name.EndsWith(".repx", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files == null || files.Count == 0)
                return null;
            var oldIds = new HashSet<Guid>(oldObject.ActualFileSnapshot?.Files?.Select(f => f.Id) ?? Enumerable.Empty<Guid>());
            return files.FirstOrDefault(f => !oldIds.Contains(f.Id)) ?? files.First();
        }
    }
}
