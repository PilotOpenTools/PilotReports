using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Reports.Infrastructure;

namespace Reports.Publishing
{
    /// <summary>Токен GitLab: Personal Access Token или OAuth (с refresh-токеном).</summary>
    [DataContract]
    internal sealed class StoredToken
    {
        public const string Pat = "pat";
        public const string OAuth = "oauth";
        public const string Git = "git";
        /// <summary>Пользователь вышел (или GitLab отклонил токен): учётные данные git для этого сервера больше не подставляются.</summary>
        public const string LoggedOut = "logout";

        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "access")] public string AccessToken { get; set; }
        [DataMember(Name = "refresh")] public string RefreshToken { get; set; }
        [DataMember(Name = "verifier")] public string CodeVerifier { get; set; }
        [DataMember(Name = "expires")] public DateTime? ExpiresUtc { get; set; }
        [DataMember(Name = "user")] public string GitLabUser { get; set; }

        public bool IsExpiring => ExpiresUtc.HasValue && ExpiresUtc.Value <= DateTime.UtcNow.AddMinutes(1);
    }

    /// <summary>
    /// Хранилище токена GitLab. Токен принадлежит <b>одному пользователю</b>:
    /// <list type="bullet">
    /// <item>файл лежит в профиле Windows (%AppData%\ASCON\Reports\gitlab\) - у каждой учётной записи Windows свой;</item>
    /// <item>шифруется DPAPI с областью CurrentUser - другая учётная запись Windows не расшифрует его, даже скопировав файл;</item>
    /// <item>имя файла включает логин Pilot и сервер GitLab - если под одной учётной записью Windows работают разные
    /// пользователи Pilot, у каждого свой токен.</item>
    /// </list>
    /// Если своего токена нет, берётся пароль/токен из менеджера учётных данных git (он тоже хранится у текущего
    /// пользователя Windows), это можно выключить настройкой useGitCredentials.
    /// </summary>
    internal static class GitLabTokenStore
    {
        private static readonly string Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ASCON", "Reports", "gitlab");

        // Энтропия DPAPI: файл не расшифровать другим приложением того же пользователя «вслепую».
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Reports.GitLabToken.v2");

        public static StoredToken Load(string pilotLogin, string baseUrl, bool useGitCredentials)
        {
            var saved = Read(PathFor(pilotLogin, baseUrl));
            if (saved?.Kind == StoredToken.LoggedOut)
                return null;
            if (saved != null && !string.IsNullOrEmpty(saved.AccessToken))
                return saved;

            if (!useGitCredentials)
                return null;
            var fromGit = FromGitCredentials(baseUrl);
            return string.IsNullOrEmpty(fromGit) ? null : new StoredToken { Kind = StoredToken.Git, AccessToken = fromGit };
        }

        public static void Save(string pilotLogin, string baseUrl, StoredToken token)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = Serialize(token);
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PathFor(pilotLogin, baseUrl), data);
        }

        /// <summary>
        /// Выход: токен удаляется, вместо него пишется отметка «вышел». Без неё модуль сразу снова подставил бы
        /// пароль из менеджера учётных данных git - и «Выйти» не меняло бы ничего, если GitLab этот пароль отклоняет.
        /// Отметку снимает следующий вход (токеном или через браузер).
        /// </summary>
        public static void Clear(string pilotLogin, string baseUrl)
        {
            try
            {
                Save(pilotLogin, baseUrl, new StoredToken { Kind = StoredToken.LoggedOut });
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось записать выход из GitLab", ex);
                try { File.Delete(PathFor(pilotLogin, baseUrl)); } catch { /* файла нет */ }
            }
        }

        /// <summary>Файл токена для пары «логин Pilot + сервер GitLab» (имя - хеш, чтобы не светить логин).</summary>
        internal static string PathFor(string pilotLogin, string baseUrl)
        {
            var key = $"{(pilotLogin ?? "").Trim().ToLowerInvariant()}|{(baseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant()}";
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
                return Path.Combine(Directory, hash + ".dat");
            }
        }

        private static StoredToken Read(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return (StoredToken)new DataContractJsonSerializer(typeof(StoredToken)).ReadObject(stream);
            }
            catch (Exception ex)
            {
                // Чужой файл (другая учётка Windows) или повреждён - считаем, что токена нет.
                ModuleLog.Warn("Не удалось прочитать сохранённый токен GitLab", ex);
                return null;
            }
        }

        private static string Serialize(StoredToken token)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(StoredToken)).WriteObject(stream, token);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private const int GitTimeoutMs = 5000;

        /// <summary>Пароль/токен из git credential helper, без диалогов (если там ничего нет - null).</summary>
        private static string FromGitCredentials(string baseUrl)
        {
            try
            {
                var git = FindGit(Environment.GetEnvironmentVariable("PATH"));
                if (git == null)
                    return null; // git не установлен - войдут через браузер или токеном

                var uri = new Uri(baseUrl);
                var start = new ProcessStartInfo(git, "credential fill")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                start.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
                using (var process = Process.Start(start))
                {
                    process.StandardInput.Write($"protocol={uri.Scheme}\nhost={uri.Authority}\n\n");
                    process.StandardInput.Close();
                    // Вывод читаем асинхронно: зависший git не должен держать поток дольше тайм-аута.
                    var stderr = process.StandardError.ReadToEndAsync();
                    var output = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(GitTimeoutMs) || !output.Wait(GitTimeoutMs) || process.ExitCode != 0)
                    {
                        if (!process.HasExited)
                            process.Kill();
                        ModuleLog.Warn("git credential fill не вернул учётные данные за " + GitTimeoutMs / 1000 + " с");
                        return null;
                    }
                    stderr.Wait(GitTimeoutMs);
                    foreach (var line in output.Result.Split('\n'))
                    {
                        if (line.StartsWith("password=", StringComparison.Ordinal))
                            return line.Substring("password=".Length).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("git credential fill не выполнен", ex);
            }
            return null;
        }

        /// <summary>
        /// Полный путь к git.exe по каталогам PATH. CreateProcess с голым именем сначала смотрит в каталог приложения
        /// и текущий каталог - там git.exe мог подложить кто угодно, поэтому ищем только в PATH.
        /// </summary>
        internal static string FindGit(string pathVariable)
        {
            foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var folder = dir.Trim().Trim('"');
                    if (folder.Length == 0 || !Path.IsPathRooted(folder))
                        continue;
                    var candidate = Path.Combine(folder, "git.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // недопустимый символ в каталоге PATH - пропускаем его
                }
            }
            return null;
        }
    }
}
