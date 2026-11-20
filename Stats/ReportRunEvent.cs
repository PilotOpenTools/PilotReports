using System;
using System.Globalization;
using System.Text;

namespace Reports.Stats
{
    /// <summary>
    /// Итог одного построения отчёта. Уходит в сервис report-metrics-collector
    /// и хранится в таблице metrics.report_events.
    /// </summary>
    internal sealed class ReportRunEvent
    {
        public Guid RunId { get; set; }
        public DateTime StartedUtc { get; set; }
        public string Report { get; set; }
        public Guid? ReportId { get; set; }
        public string Status { get; set; }
        public long DurationMs { get; set; }
        public int? Pages { get; set; }
        public string ErrorType { get; set; }
        public string ErrorMessage { get; set; }
        public string Login { get; set; }
        public string Person { get; set; }
        public string Server { get; set; }
        public string ClientType { get; set; }
        public string ClientVersion { get; set; }
        public string ModuleVersion { get; set; }
        public string Machine { get; set; }

        /// <summary>JSON в формате, который ждёт сервис (snake_case).</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(512).Append('{');
            Json.Prop(sb, "run_id", RunId.ToString());
            Json.Prop(sb, "ts", StartedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            Json.Prop(sb, "report", Report);
            if (ReportId.HasValue)
                Json.Prop(sb, "report_id", ReportId.Value.ToString());
            Json.Prop(sb, "status", Status);
            Json.Prop(sb, "duration_ms", DurationMs);
            if (Pages.HasValue)
                Json.Prop(sb, "pages", Pages.Value);
            Json.Prop(sb, "error_type", ErrorType);
            Json.Prop(sb, "error_message", ErrorMessage);
            Json.Prop(sb, "login", Login);
            Json.Prop(sb, "person", Person);
            Json.Prop(sb, "server", Server);
            Json.Prop(sb, "client_type", ClientType);
            Json.Prop(sb, "client_version", ClientVersion);
            Json.Prop(sb, "module_version", ModuleVersion);
            Json.Prop(sb, "machine", Machine);
            sb.Length--; // последняя запятая
            return sb.Append('}').ToString();
        }
    }

    /// <summary>
    /// Минимальная запись JSON без сторонних библиотек: в общем AppDomain клиента Pilot
    /// чужие версии Newtonsoft.Json/System.Text.Json - частый источник конфликтов.
    /// </summary>
    internal static class Json
    {
        public static void Prop(StringBuilder sb, string name, string value)
        {
            if (value == null)
                return;
            Escape(sb, name);
            sb.Append(':');
            Escape(sb, value);
            sb.Append(',');
        }

        public static void Prop(StringBuilder sb, string name, long value)
        {
            Escape(sb, name);
            sb.Append(':').Append(value.ToString(CultureInfo.InvariantCulture)).Append(',');
        }

        public static void Escape(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
