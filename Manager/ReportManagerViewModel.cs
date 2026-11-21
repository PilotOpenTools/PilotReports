using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Reports.Infrastructure;
using Reports.Publishing;
using IDataObject = Ascon.Pilot.SDK.IDataObject;

namespace Reports.Manager
{
    /// <summary>Строка истории версий отчёта в Pilot.</summary>
    internal sealed class SnapshotRow
    {
        public DateTime Created { get; set; }
        public string Author { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Окно «Загрузить отчёт» (главное меню Pilot): дерево папок и отчётов, для выбранного отчёта -
    /// загрузка .repx с диска и сравнение/обновление из веток GitLab main и dev.
    /// Каждое обновление создаёт в Pilot новую версию отчёта с причиной.
    /// </summary>
    internal sealed class ReportManagerViewModel : INotifyPropertyChanged
    {
        private const string Main = ReportPublishService.MainBranch;
        private const string Dev = ReportPublishService.DevBranch;

        private readonly ModuleConfig _config;
        private readonly string _pilotLogin;
        private List<ReportNode> _all = new List<ReportNode>();
        private List<ReportNode> _roots = new List<ReportNode>();
        private string _filterText;
        private ReportNode _selected;
        private int _selectionVersion;
        private GitLabAuth _auth;
        private ReportPublishService _service;
        private ReportRepository _repo;
        private string _account = "GitLab: вход не выполнен";
        private string _pilotInfo;
        private List<SnapshotRow> _history = new List<SnapshotRow>();
        private string _repoInfo;
        private string _status;
        private string _treeInfo;
        private bool _isBusy;
        private bool _repoMissing;

        public ReportManagerViewModel(ModuleConfig config, string pilotLogin)
        {
            _config = config;
            _pilotLogin = pilotLogin;

            RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
            UploadCommand = new AsyncCommand(UploadFromDiskAsync, () => !IsBusy && HasReport);
            CompareMainCommand = new AsyncCommand(() => CompareAsync(Main), () => CanUseBranch(Main));
            UpdateMainCommand = new AsyncCommand(() => UpdateFromBranchAsync(Main, null), () => CanUseBranch(Main));
            CompareDevCommand = new AsyncCommand(() => CompareAsync(Dev), () => CanUseBranch(Dev));
            UpdateDevCommand = new AsyncCommand(() => UpdateFromBranchAsync(Dev, null), () => CanUseBranch(Dev));
            CreateRepoCommand = new AsyncCommand(CreateRepoAsync, () => !IsBusy && HasReport && RepoMissing);
            SaveLocalCommand = new AsyncCommand(() => { SaveLocal(); return Task.CompletedTask; }, () => !IsBusy && HasReport);

            LoginBrowserCommand = new AsyncCommand(LoginInBrowserAsync, () => !IsBusy && _auth?.BrowserLoginAvailable == true);
            SetTokenCommand = new AsyncCommand(SetTokenAsync, () => !IsBusy && _auth != null);
            LogoutCommand = new AsyncCommand(LogoutAsync, () => !IsBusy && _auth != null);
            OpenRepoCommand = new AsyncCommand(() => { if (RepoUrl != null) ExternalLink.Open(RepoUrl); return Task.CompletedTask; }, () => RepoUrl != null);
        }

        /// <summary>Окно-владелец для диалогов.</summary>
        public Func<Window> Owner { get; set; }

        public List<ReportNode> Roots
        {
            get => _roots;
            private set { _roots = value; OnPropertyChanged(); }
        }

        /// <summary>Поиск по имени отчёта или папки.</summary>
        public string FilterText
        {
            get => _filterText;
            set { _filterText = value; OnPropertyChanged(); ApplyFilter(); }
        }

        /// <summary>Итог загрузки дерева (под деревом, чтобы его не затирали другие сообщения).</summary>
        public string TreeInfo
        {
            get => _treeInfo;
            private set { _treeInfo = value; OnPropertyChanged(); }
        }

        public bool HasReport => _selected != null && !_selected.IsFolder;
        public string ReportTitle => HasReport ? _selected.Title : "Выберите отчёт в дереве слева";

        public string PilotInfo
        {
            get => _pilotInfo;
            private set { _pilotInfo = value; OnPropertyChanged(); }
        }

        public List<SnapshotRow> History
        {
            get => _history;
            private set { _history = value; OnPropertyChanged(); }
        }

        public string RepoInfo
        {
            get => _repoInfo;
            private set { _repoInfo = value; OnPropertyChanged(); }
        }

        public string RepoUrl => _repo?.Project.WebUrl;

        public string Account
        {
            get => _account;
            private set { _account = value; OnPropertyChanged(); }
        }

        public bool BrowserLoginAvailable => _auth?.BrowserLoginAvailable == true;

        public string Status
        {
            get => _status;
            private set { _status = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
        }

        public ICommand RefreshCommand { get; }
        public ICommand UploadCommand { get; }
        public ICommand CompareMainCommand { get; }
        public ICommand UpdateMainCommand { get; }
        public ICommand CompareDevCommand { get; }
        public ICommand UpdateDevCommand { get; }
        public ICommand SaveLocalCommand { get; }
        public ICommand CreateRepoCommand { get; }

        /// <summary>Вход выполнен, а репозитория выбранного отчёта нет - предлагается создать.</summary>
        public bool RepoMissing
        {
            get => _repoMissing;
            private set { _repoMissing = value; OnPropertyChanged(); }
        }
        public ICommand LoginBrowserCommand { get; }
        public ICommand SetTokenCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand OpenRepoCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>При открытии окна: дерево отчётов из Pilot, затем вход в GitLab.</summary>
        public async Task InitializeAsync()
        {
            await RefreshAsync();
            await ConnectGitLabAsync();
        }

        // ------------------------------------------------------------------ дерево и выбор

        private async Task RefreshAsync()
        {
            var selectedId = _selected?.Id;
            TreeInfo = "Загрузка списка отчётов…";
            await RunAsync(() =>
            {
                try
                {
                    _all = PilotReports.LoadTree();
                }
                catch (Exception ex)
                {
                    TreeInfo = "Не удалось загрузить отчёты: " + ex.Message;
                    throw;
                }
                ApplyFilter();
                var count = Flatten(_all).Count(n => !n.IsFolder);
                TreeInfo = _all.Count == 0 ? "Папка отчётов не найдена в базе Pilot (или нет доступа)." : $"Отчётов: {count}";
                return Task.CompletedTask;
            }, "Загрузка списка отчётов");

            var same = selectedId == null ? null : Flatten(_all).FirstOrDefault(n => n.Id == selectedId);
            await SelectAsync(same);
        }

        private void ApplyFilter()
        {
            var q = FilterText?.Trim();
            if (string.IsNullOrEmpty(q))
            {
                Roots = _all;
                return;
            }
            Roots = _all.Select(n => Filter(n, q)).Where(n => n != null).ToList();
        }

        private static ReportNode Filter(ReportNode node, string q)
        {
            var match = node.Title?.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
            if (!node.IsFolder)
                return match ? node : null;
            if (match)
                return node;
            var children = node.Children.Select(c => Filter(c, q)).Where(c => c != null).ToList();
            return children.Count == 0 ? null : node.WithChildren(children);
        }

        private static IEnumerable<ReportNode> Flatten(IEnumerable<ReportNode> nodes) =>
            nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

        /// <summary>Выбор узла дерева: сведения из Pilot сразу, репозиторий GitLab - вдогонку.</summary>
        public async Task SelectAsync(ReportNode node)
        {
            var version = ++_selectionVersion;
            _selected = node;
            _repo = null;
            RepoMissing = false;
            OnPropertyChanged(nameof(HasReport));
            OnPropertyChanged(nameof(ReportTitle));
            OnPropertyChanged(nameof(RepoUrl));
            CommandManager.InvalidateRequerySuggested();

            if (!HasReport)
            {
                PilotInfo = node?.IsFolder == true ? $"Папка: {node.Title}" : null;
                History = new List<SnapshotRow>();
                RepoInfo = null;
                return;
            }

            ShowPilotInfo(node.Object);
            await FindRepoAsync(version);
        }

        private void ShowPilotInfo(IDataObject report)
        {
            var actual = report.ActualFileSnapshot;
            var rows = new List<SnapshotRow>();
            if (actual != null)
                rows.Add(Row(actual));
            if (report.PreviousFileSnapshots != null)
                rows.AddRange(report.PreviousFileSnapshots.Select(Row).OrderByDescending(r => r.Created));
            History = rows;

            var file = PilotReports.CurrentRepx(report);
            PilotInfo = actual == null
                ? "В отчёте нет файлов."
                : $"В Pilot: версия от {Local(actual.Created):dd.MM.yyyy HH:mm}, {PersonName(actual.CreatorId)}" +
                  (file == null ? " · нет файла .repx" : $" · {file.Name}, {file.Size / 1024.0:0.#} КБ");
        }

        private static SnapshotRow Row(Ascon.Pilot.SDK.IFilesSnapshot s) =>
            new SnapshotRow { Created = Local(s.Created), Author = PersonName(s.CreatorId), Reason = s.Reason };

        private static DateTime Local(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();

        private static string PersonName(int personId)
        {
            try { return PilotApi.Repository.GetPerson(personId)?.DisplayName ?? $"#{personId}"; }
            catch { return $"#{personId}"; }
        }

        // ------------------------------------------------------------------ GitLab

        private async Task ConnectGitLabAsync()
        {
            _service = null;
            _auth = null;
            if (!ReportPublishService.TryParseGroupUrl(_config.ReportsGitLabGroupUrl, out var baseUrl, out var groupPath))
            {
                Account = "GitLab: не указаны сервер и группа с отчётами - их задаёт администратор в общих настройках «PilotOpenTools отчёты (GitLab)».";
                OnPropertyChanged(nameof(BrowserLoginAvailable));
                return;
            }

            _auth = new GitLabAuth(_pilotLogin, baseUrl, _config);
            OnPropertyChanged(nameof(BrowserLoginAvailable));

            StoredToken token = null;
            await RunAsync(async () => token = await _auth.GetAsync(), "Проверка входа в GitLab");
            if (token == null)
            {
                Account = BrowserLoginAvailable
                    ? $"GitLab ({baseUrl}): вход не выполнен - «Войти через браузер» или «Токен GitLab…»."
                    : $"GitLab ({baseUrl}): нет токена - нажмите «Токен GitLab…» (Personal Access Token, scope api).";
            }
            else
            {
                Account = $"GitLab: {(string.IsNullOrEmpty(token.GitLabUser) ? "вход выполнен" : token.GitLabUser)} · " +
                          (token.Kind == StoredToken.OAuth ? "вход через браузер" : token.Kind == StoredToken.Git ? "учётные данные git" : "личный токен");
                _service = new ReportPublishService(new GitLabApi(baseUrl, token.AccessToken), groupPath);
            }

            if (HasReport)
                await FindRepoAsync(_selectionVersion);
        }

        private async Task FindRepoAsync(int version)
        {
            if (_service == null)
            {
                RepoInfo = "Сравнение и обновление из GitLab доступны после входа в GitLab.";
                return;
            }

            var name = _selected.Title;
            RepoInfo = "Поиск репозитория в GitLab…";
            await RunAsync(async () =>
            {
                var repo = await _service.FindAsync(name);
                if (version != _selectionVersion)
                    return; // пока искали, выбрали другой отчёт
                _repo = repo;
                OnPropertyChanged(nameof(RepoUrl));
                if (repo == null)
                {
                    RepoMissing = true;
                    RepoInfo = $"Репозиторий «{name}» не найден в группе {_service.GroupPath} - нажмите «Создать репозиторий…».";
                    return;
                }
                ShowRepo();
            }, "Поиск репозитория");
        }

        private void ShowRepo()
        {
            var folder = string.IsNullOrEmpty(_repo.Folder) ? _service.GroupPath : $"{_service.GroupPath}/{_repo.Folder}";
            var branches = string.Join(", ", new[] { Dev, Main }
                .Select(b => _repo.Branches.Contains(b) ? (_repo.RepxPathByBranch.ContainsKey(b) ? b : $"{b} (без отчёта)") : $"нет ветки {b}"));
            RepoInfo = $"GitLab: {folder}/{_repo.Project.Name}  ·  ветки: {branches}";
        }

        private Task CreateRepoAsync()
        {
            var node = _selected;
            string pilotFolder = null;
            try { pilotFolder = PilotApi.LoadObject(node.Object.ParentId, TimeSpan.FromSeconds(5))?.DisplayName; }
            catch { /* подсказка папки необязательна */ }

            var window = new CreateRepoWindow(_service, node.Title, pilotFolder) { Owner = Owner?.Invoke() };
            if (window.ShowDialog() == true && window.Repository != null && _selected == node)
            {
                _repo = window.Repository;
                RepoMissing = false;
                OnPropertyChanged(nameof(RepoUrl));
                ShowRepo();
                Status = $"Репозиторий создан: {_repo.Project.WebUrl}. Отправьте отчёт в dev: «Загрузить .repx с диска…» или сохранение в дизайнере.";
                CommandManager.InvalidateRequerySuggested();
            }
            return Task.CompletedTask;
        }

        private bool CanUseBranch(string branch) =>
            !IsBusy && HasReport && _repo != null && _repo.RepxPathByBranch.ContainsKey(branch);

        // ------------------------------------------------------------------ пять кнопок

        /// <summary>1. Загрузить .repx с диска: проводник / проверка файла / причина / новая версия в Pilot.</summary>
        private async Task UploadFromDiskAsync()
        {
            var node = _selected;
            var dialog = new OpenFileDialog
            {
                Title = $"Загрузить .repx в отчёт «{node.Title}»",
                Filter = "Отчёт DevExpress (*.repx)|*.repx|Все файлы|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(Owner?.Invoke()) != true)
                return;

            byte[] content;
            try
            {
                content = File.ReadAllBytes(dialog.FileName);
            }
            catch (Exception ex)
            {
                Status = $"Не удалось прочитать файл: {ex.Message}";
                return;
            }

            var problem = RepxText.Validate(content);
            if (problem != null)
            {
                MessageBox.Show(Owner?.Invoke(), problem, "Загрузка отчёта", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (IsSameAsPilot(node, content))
            {
                MessageBox.Show(Owner?.Invoke(), "Файл совпадает с текущей версией отчёта в Pilot - обновлять нечего.",
                    "Загрузка отчёта", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var reasonWindow = new ReasonWindow(
                $"Загрузить «{Path.GetFileName(dialog.FileName)}» в отчёт «{node.Title}»",
                "В Pilot будет создана новая версия отчёта. Затем откроется окно отправки в GitLab - причина подставится в «Что изменилось».",
                okText: "Загрузить в Pilot") { Owner = Owner?.Invoke() };
            if (reasonWindow.ShowDialog() != true)
                return;

            await UpdatePilotAsync(node, content, reasonWindow.Reason,
                new PendingSave { OfferPublish = true, Message = reasonWindow.Reason },
                $"с диска: {dialog.FileName}");
        }

        /// <summary>2, 4. Сравнить отчёт в Pilot с веткой; из окна сравнения можно сразу обновить.</summary>
        private async Task CompareAsync(string branch)
        {
            var node = _selected;
            byte[] gitlab = null, pilot = null;
            await RunAsync(async () =>
            {
                gitlab = await _service.GetRepxAsync(_repo, branch);
                pilot = PilotReports.ReadCurrent(node.Object);
            }, $"Загрузка отчёта из {branch}");
            if (gitlab == null)
            {
                Status ??= $"В ветке {branch} нет файла отчёта.";
                return;
            }
            if (pilot != null && pilot.SequenceEqual(gitlab))
            {
                MessageBox.Show(Owner?.Invoke(), $"Отчёт в Pilot совпадает с веткой {branch} байт в байт.",
                    $"Сравнение с {branch}", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new DiffWindow(node.Title, branch, RepxText.Parse(pilot), RepxText.Parse(gitlab), $"Обновить Pilot из {branch}…")
            {
                Owner = Owner?.Invoke(),
            };
            if (window.ShowDialog() == true)
                await UpdateFromBranchAsync(branch, gitlab);
        }

        /// <summary>3, 5. Обновить отчёт в Pilot файлом из ветки (с причиной; в GitLab обратно не отправляется).</summary>
        private async Task UpdateFromBranchAsync(string branch, byte[] content)
        {
            var node = _selected;
            GitLabCommit last = null;
            await RunAsync(async () =>
            {
                if (content == null)
                    content = await _service.GetRepxAsync(_repo, branch);
                last = await _service.LastCommitAsync(_repo, branch);
            }, $"Загрузка отчёта из {branch}");
            if (content == null)
            {
                Status ??= $"В ветке {branch} нет файла отчёта.";
                return;
            }
            if (IsSameAsPilot(node, content))
            {
                MessageBox.Show(Owner?.Invoke(), $"Отчёт в Pilot уже совпадает с веткой {branch} - обновлять нечего.",
                    $"Обновление из {branch}", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var source = last == null ? branch : $"{branch} ({last.ShortId}: {last.Title})";
            var reasonWindow = new ReasonWindow(
                $"Обновить отчёт «{node.Title}» из GitLab {branch}",
                $"Источник: {source}." + (branch == Main ? "\nmain - продовая версия отчёта." : "\ndev - тестовая версия отчёта."),
                $"Обновлено из GitLab {source}",
                $"Обновить из {branch}") { Owner = Owner?.Invoke() };
            if (reasonWindow.ShowDialog() != true)
                return;

            await UpdatePilotAsync(node, content, reasonWindow.Reason, new PendingSave { OfferPublish = false }, $"из GitLab {source}");
        }

        private async Task UpdatePilotAsync(ReportNode node, byte[] content, string reason, PendingSave intent, string source)
        {
            var updated = false;
            await RunAsync(() =>
            {
                PilotReports.Update(node.Object, content, reason, intent);
                updated = true;
                ModuleLog.Info($"Отчёт «{node.Title}» обновлён в Pilot {source}. Причина: {reason}");
                return Task.CompletedTask;
            }, "Обновление отчёта в Pilot");
            if (!updated)
                return;

            Status = $"Отчёт «{node.Title}» обновлён в Pilot. Причина: {reason}";
            // Новая версия приходит в клиент не мгновенно - перечитываем объект чуть позже.
            await Task.Delay(700);
            var fresh = PilotApi.LoadObject(node.Id);
            if (fresh != null)
            {
                node.Object = fresh;
                if (_selected?.Id == node.Id)
                    ShowPilotInfo(fresh);
            }
        }

        private static bool IsSameAsPilot(ReportNode node, byte[] content)
        {
            try
            {
                var current = PilotReports.ReadCurrent(node.Object);
                return current != null && current.SequenceEqual(content);
            }
            catch (Exception ex)
            {
                ModuleLog.Warn($"Не удалось прочитать текущий .repx отчёта «{node.Title}»", ex);
                return false;
            }
        }

        private void SaveLocal()
        {
            var node = _selected;
            var content = PilotReports.ReadCurrent(node.Object);
            if (content == null)
            {
                Status = "В отчёте нет файла .repx.";
                return;
            }
            var dialog = new SaveFileDialog
            {
                Title = $"Сохранить отчёт «{node.Title}»",
                FileName = ReportNames.DefaultPath(node.Title),
                Filter = "Отчёт DevExpress (*.repx)|*.repx|Все файлы|*.*",
            };
            if (dialog.ShowDialog(Owner?.Invoke()) != true)
                return;
            File.WriteAllBytes(dialog.FileName, content);
            Status = $"Файл сохранён: {dialog.FileName}";
        }

        // ------------------------------------------------------------------ вход в GitLab

        private async Task SetTokenAsync()
        {
            var window = new TokenWindow { Owner = Owner?.Invoke() };
            if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.Token))
                return;
            await RunAsync(async () =>
            {
                var token = await _auth.LoginWithTokenAsync(window.Token);
                Status = $"Токен сохранён для {token.GitLabUser}.";
            }, "Проверка токена");
            await ConnectGitLabAsync();
        }

        private async Task LoginInBrowserAsync()
        {
            await RunAsync(async () =>
            {
                using (var cts = new CancellationTokenSource())
                {
                    Status = "Откроется браузер: войдите в GitLab и разрешите доступ…";
                    var token = await _auth.LoginInBrowserAsync(cts.Token);
                    Status = $"Вход выполнен: {token.GitLabUser}.";
                }
            }, "Вход через браузер");
            await ConnectGitLabAsync();
        }

        private async Task LogoutAsync()
        {
            _auth?.Logout();
            Status = "Вход в GitLab сброшен - токен удалён с этого компьютера.";
            await ConnectGitLabAsync();
        }

        // ------------------------------------------------------------------

        private async Task RunAsync(Func<Task> action, string what)
        {
            IsBusy = true;
            Status = what + "…";
            try
            {
                await action();
                if (Status == what + "…")
                    Status = null;
            }
            catch (GitLabApiException ex)
            {
                Status = ex.Message;
                if (ex.Status == 401)
                    _auth?.Logout();
            }
            catch (Exception ex)
            {
                ModuleLog.Error($"{what}: окно «Загрузить отчёт»", ex);
                Status = $"{what}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
