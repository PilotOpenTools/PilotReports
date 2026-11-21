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

namespace Reports.Publishing
{
    /// <summary>
    /// Окно при сохранении отчёта: отправить .repx в ветку dev (тест) или main (прод) репозитория отчёта.
    /// Репозиторий ищется по имени отчёта в группе отчётов GitLab.
    /// </summary>
    internal sealed class PublishViewModel : INotifyPropertyChanged
    {
        private readonly byte[] _content;
        private readonly string _groupUrl;
        private readonly ModuleConfig _config;
        private readonly string _pilotLogin;
        private GitLabAuth _auth;
        private string _account = "GitLab: вход не выполнен";
        private ReportPublishService _service;
        private ReportRepository _repo;
        private string _commitMessage;
        private string _repoInfo = "Поиск репозитория в GitLab…";
        private string _status;
        private bool _isBusy;
        private bool _isMain;
        private bool _repoMissing;
        private readonly string _pilotFolder;

        public PublishViewModel(string reportName, string fileName, byte[] content, ModuleConfig config, string pilotLogin,
            string initialMessage = null, string pilotFolder = null, bool publishOnly = false, bool main = false)
        {
            _pilotFolder = pilotFolder;
            PublishOnly = publishOnly;
            _isMain = main;
            ReportName = reportName;
            _commitMessage = initialMessage;
            FileName = fileName;
            _content = content;
            _config = config;
            _groupUrl = config.ReportsGitLabGroupUrl;
            _pilotLogin = pilotLogin;
            LoginBrowserCommand = new AsyncCommand(LoginInBrowserAsync, () => !IsBusy && _auth?.BrowserLoginAvailable == true);
            LogoutCommand = new AsyncCommand(LogoutAsync, () => !IsBusy && _auth != null);

            SendCommand = new AsyncCommand(SendAsync, () => !IsBusy && _repo != null);
            CreateRepoCommand = new AsyncCommand(CreateRepoAsync, () => !IsBusy && RepoMissing);
            HistoryCommand = new AsyncCommand(ShowHistoryAsync, () => !IsBusy && _repo != null);
            SetTokenCommand = new AsyncCommand(SetTokenAsync, () => !IsBusy);
            OpenRepoCommand = new AsyncCommand(() => { if (RepoUrl != null) ExternalLink.Open(RepoUrl); return Task.CompletedTask; }, () => RepoUrl != null);
            SaveLocalCommand = new AsyncCommand(() => { SaveLocal(); return Task.CompletedTask; }, () => !IsBusy);
            SaveOnlyCommand = new AsyncCommand(() => { SnapshotReason = ReasonFrom(CommitMessage); RequestClose?.Invoke(true); return Task.CompletedTask; }, () => !IsBusy);
            CancelCommand = new AsyncCommand(() => { RequestClose?.Invoke(false); return Task.CompletedTask; }, () => !IsBusy);
        }

        public string ReportName { get; }
        public string FileName { get; }

        /// <summary>
        /// Окно открыто из PilotReport для текущей версии отчёта, а не при сохранении в дизайнере:
        /// в Pilot ничего не сохраняется, есть только «Отправить» и «Закрыть».
        /// </summary>
        public bool PublishOnly { get; }
        public bool SaveMode => !PublishOnly;
        public string WindowTitle => PublishOnly ? "Отправка отчёта в GitLab" : "Сохранение отчёта - отправка в GitLab";
        public string SendText => PublishOnly ? "Отправить" : "Сохранить и отправить";
        public string MessageLabel => PublishOnly
            ? "Что изменилось (коммит и CHANGELOG.md; каждая строка - отдельный пункт):"
            : "Что изменилось (причина версии в Pilot, коммит и CHANGELOG.md; каждая строка - отдельный пункт):";

        public bool IsDev
        {
            get => !_isMain;
            set { if (value) IsMain = false; }
        }

        public bool IsMain
        {
            get => _isMain;
            set
            {
                if (_isMain == value) return;
                _isMain = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDev));
                OnPropertyChanged(nameof(BranchHint));
            }
        }

        /// <summary>Прод - только через hotfix-ветку от main (имя - один раз на окно).</summary>
        private readonly string _hotfixBranch = ReportPublishService.NewHotfixBranch(DateTime.Now);

        public string Branch => IsMain ? _hotfixBranch : ReportPublishService.DevBranch;

        public string BranchHint => IsMain
            ? $"Hotfix прода: отчёт уйдёт в ветку {_hotfixBranch} от main. На прод его выпустит веб-админка («Hotfix в прод» на странице отчёта)."
            : "dev = тестовый сервер. На прод отчёт идёт из веб-админки: релиз на стейдж, затем «Выпустить в прод».";

        /// <summary>
        /// Причина версии отчёта в Pilot: пункты «Что изменилось» через «; ». Заполняется, когда окно закрыто
        /// отправкой или «Только в Pilot»; null - причины нет.
        /// </summary>
        public string SnapshotReason { get; private set; }

        public string CommitMessage
        {
            get => _commitMessage;
            set { _commitMessage = value; OnPropertyChanged(); }
        }

        public string RepoInfo
        {
            get => _repoInfo;
            private set { _repoInfo = value; OnPropertyChanged(); }
        }

        public string RepoUrl => _repo?.Project.WebUrl;

        /// <summary>Под кем выполнен вход в GitLab (токен у каждого пользователя свой).</summary>
        public string Account
        {
            get => _account;
            private set { _account = value; OnPropertyChanged(); }
        }

        /// <summary>Кнопка «Войти через браузер» - если в конфиге указано OAuth-приложение GitLab.</summary>
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

        public ICommand SendCommand { get; }
        public ICommand HistoryCommand { get; }
        public ICommand CreateRepoCommand { get; }

        /// <summary>Вход выполнен, а репозитория отчёта нет - предлагается создать.</summary>
        public bool RepoMissing
        {
            get => _repoMissing;
            private set { _repoMissing = value; OnPropertyChanged(); }
        }
        public ICommand SetTokenCommand { get; }
        public ICommand LoginBrowserCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand OpenRepoCommand { get; }
        public ICommand SaveLocalCommand { get; }
        public ICommand SaveOnlyCommand { get; }
        public ICommand CancelCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<bool?> RequestClose;

        /// <summary>Вызывается при открытии окна: токен / поиск репозитория по имени отчёта.</summary>
        public async Task InitializeAsync()
        {
            if (!ReportPublishService.TryParseGroupUrl(_groupUrl, out var baseUrl, out var groupPath))
            {
                RepoInfo = "Не указаны сервер и группа GitLab с отчётами - их задаёт администратор в общих настройках «PilotOpenTools отчёты (GitLab)».";
                return;
            }

            RepoMissing = false;
            _auth = new GitLabAuth(_pilotLogin, baseUrl, _config);
            OnPropertyChanged(nameof(BrowserLoginAvailable));
            _repo = null;
            OnPropertyChanged(nameof(RepoUrl));

            StoredToken token = null;
            await RunAsync(async () => token = await _auth.GetAsync(), "Проверка входа в GitLab");
            if (token == null)
            {
                Account = "GitLab: вход не выполнен";
                RepoInfo = BrowserLoginAvailable
                    ? $"Войдите в GitLab ({baseUrl}): «Войти через браузер» или «Токен GitLab…»."
                    : $"Нет токена GitLab для {baseUrl}. Нажмите «Токен GitLab…» и вставьте Personal Access Token (scope api).";
                return;
            }
            Account = $"GitLab: {(string.IsNullOrEmpty(token.GitLabUser) ? "вход выполнен" : token.GitLabUser)} · " +
                      (token.Kind == StoredToken.OAuth ? "вход через браузер" : token.Kind == StoredToken.Git ? "учётные данные git" : "личный токен") +
                      $" · пользователь Pilot {_pilotLogin}";

            _service = new ReportPublishService(new GitLabApi(baseUrl, token.AccessToken), groupPath);
            _tokenRejected = false;
            await RunAsync(async () =>
            {
                _repo = await _service.FindAsync(ReportName);
                OnPropertyChanged(nameof(RepoUrl));
                if (_repo == null)
                {
                    RepoMissing = true;
                    RepoInfo = $"Репозиторий «{ReportName}» не найден в группе {_service.GroupPath} - нажмите «Создать репозиторий…».";
                    return;
                }
                ShowRepo();
            }, "Поиск репозитория");

            if (_tokenRejected)
            {
                // GitLab не принял токен: вход сброшен (RunAsync), показываем, как войти заново.
                _service = null;
                Account = "GitLab: вход не выполнен";
                RepoInfo = (token.Kind == StoredToken.Git
                               ? "GitLab не принял пароль из менеджера учётных данных git (API GitLab принимает только токен). "
                               : "GitLab не принял токен. ") +
                           (BrowserLoginAvailable
                               ? "Войдите: «Войти через браузер» или «Токен GitLab…»."
                               : "Нажмите «Токен GitLab…» и вставьте Personal Access Token (scope api).");
            }
        }

        private bool _tokenRejected;

        private void ShowRepo()
        {
            var folder = string.IsNullOrEmpty(_repo.Folder) ? _service.GroupPath : $"{_service.GroupPath}/{_repo.Folder}";
            var branches = string.Join(", ", new[] { ReportPublishService.DevBranch, ReportPublishService.MainBranch }
                .Select(b => _repo.Branches.Contains(b) ? (_repo.RepxPathByBranch.ContainsKey(b) ? b : $"{b} (без отчёта)") : $"нет ветки {b} (создастся при отправке)"));
            RepoInfo = $"Репозиторий: {folder}/{_repo.Project.Name}  ·  ветки: {branches}";
        }

        private Task CreateRepoAsync()
        {
            var window = new CreateRepoWindow(_service, ReportName, _pilotFolder)
            {
                Owner = Application.Current?.Windows.OfType<PublishWindow>().FirstOrDefault(),
            };
            if (window.ShowDialog() == true && window.Repository != null)
            {
                _repo = window.Repository;
                RepoMissing = false;
                OnPropertyChanged(nameof(RepoUrl));
                ShowRepo();
                Status = $"Репозиторий создан: {_repo.Project.WebUrl}";
                CommandManager.InvalidateRequerySuggested();
            }
            return Task.CompletedTask;
        }

        private async Task SendAsync()
        {
            var message = CommitMessage?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                Status = "Опишите изменение - оно попадёт в сообщение коммита.";
                return;
            }
            if (IsMain && MessageBox.Show(
                    $"Отправить отчёт как hotfix прода (ветка {_hotfixBranch} от main)?\n\nНа прод он попадёт после «Hotfix в прод» в веб-админке. Обычные изменения отправляют в dev.",
                    "Hotfix прода", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await RunAsync(async () =>
            {
                // Строки описания - пункты CHANGELOG.md; в заголовке коммита они через «; », как в веб-админке.
                var notes = ReportChangelog.SplitNotes(message);
                var title = $"{ReportName}: {string.Join("; ", notes)}";
                var commitMessage = notes.Count > 1 ? title + "\n\n" + string.Join("\n", notes.Select(n => "- " + n)) : title;
                var created = !_repo.Branches.Contains(Branch);
                var commit = await _service.PublishAsync(_repo, Branch, _content, commitMessage, notes);
                var text = commit == null
                    ? $"В ветке {Branch} уже этот отчёт - коммит не нужен."
                    : $"Отчёт отправлен в ветку {Branch}{(created ? " (ветка создана)" : "")}: коммит {commit.ShortId}.";
                ModuleLog.Info($"Отчёт «{ReportName}»: {text}");
                MessageBox.Show(text, "Отправка в GitLab", MessageBoxButton.OK, MessageBoxImage.Information);
                SnapshotReason = ReasonFrom(message);
                RequestClose?.Invoke(true);
            }, "Отправка в GitLab");
        }

        private async Task ShowHistoryAsync()
        {
            await RunAsync(async () =>
            {
                var history = new List<GitLabCommit>();
                foreach (var branch in new[] { ReportPublishService.DevBranch, ReportPublishService.MainBranch })
                {
                    foreach (var c in await _service.HistoryAsync(_repo, branch))
                    {
                        c.Title = $"[{branch}] {c.Title}";
                        history.Add(c);
                    }
                }
                history.Sort((a, b) => Nullable.Compare(b.Date, a.Date));
                if (history.Count == 0)
                {
                    MessageBox.Show("Коммитов по отчёту нет.", "История изменений", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                new HistoryWindow(history) { Owner = Application.Current?.Windows.OfType<PublishWindow>().FirstOrDefault() }.ShowDialog();
            }, "История");
        }

        private async Task SetTokenAsync()
        {
            var window = new TokenWindow { Owner = Application.Current?.Windows.OfType<PublishWindow>().FirstOrDefault() };
            if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.Token))
                return;

            if (_auth == null)
                return;
            await RunAsync(async () =>
            {
                var token = await _auth.LoginWithTokenAsync(window.Token);
                Status = $"Токен сохранён для {token.GitLabUser}.";
            }, "Проверка токена");
            await InitializeAsync();
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
            await InitializeAsync();
        }

        private async Task LogoutAsync()
        {
            _auth?.Logout();
            Status = "Вход в GitLab сброшен - токен удалён с этого компьютера, учётные данные git больше не подставляются.";
            await InitializeAsync();
        }

        private static string ReasonFrom(string text)
        {
            var reason = string.Join("; ", ReportChangelog.SplitNotes(text ?? ""));
            if (reason.Length == 0)
                return null;
            return reason.Length > 1000 ? reason.Substring(0, 1000) + "…" : reason;
        }

        private void SaveLocal()
        {
            var dialog = new SaveFileDialog
            {
                Title = $"Сохранить отчёт «{ReportName}»",
                FileName = FileName,
                Filter = "Отчёт DevExpress (*.repx)|*.repx|Все файлы|*.*",
            };
            if (dialog.ShowDialog() != true)
                return;
            File.WriteAllBytes(dialog.FileName, _content);
            Status = $"Файл сохранён: {dialog.FileName}";
        }

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
                {
                    _auth?.Logout();
                    _tokenRejected = true;
                }
            }
            catch (Exception ex)
            {
                ModuleLog.Error($"{what}: отчёт «{ReportName}»", ex);
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

    internal sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;

        public AsyncCommand(Func<Task> execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public async void Execute(object parameter)
        {
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Команда окна отправки отчёта", ex);
                MessageBox.Show(ex.Message, "Reports", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
