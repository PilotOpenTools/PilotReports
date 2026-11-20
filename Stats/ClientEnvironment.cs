using System;
using System.Reflection;
using Ascon.Pilot.SDK;
using Reports.Infrastructure;

namespace Reports.Stats
{
    /// <summary>Сведения о клиенте, которые прикладываются к каждому событию статистики.</summary>
    internal sealed class ClientEnvironment
    {
        public static string ModuleVersion { get; } =
            typeof(ClientEnvironment).Assembly.GetName().Version?.ToString() ?? "";

        public string Login { get; set; }
        public string Person { get; set; }
        public string Server { get; set; }
        public string ClientType { get; set; }
        public string ClientVersion { get; set; }
        public string Machine { get; set; }

        public static ClientEnvironment Capture(IObjectsRepository repository, IClientInfo clientInfo)
        {
            var env = new ClientEnvironment
            {
                Machine = Environment.MachineName,
                ClientVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            };

            try
            {
                var person = repository?.GetCurrentPerson();
                env.Login = person?.Login;
                env.Person = person?.DisplayName;
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось получить текущего пользователя", ex);
            }

            try
            {
                env.Server = clientInfo?.ConnectionString?.ToString();
                env.ClientType = clientInfo?.GetClientType().ToString();
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось получить сведения о подключении", ex);
            }

            return env;
        }
    }
}
