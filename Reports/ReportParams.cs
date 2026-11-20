using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ascon.Pilot.Pilot.Reports;

namespace Reports
{
    /// <summary>
    /// Параметры отчёта DevExpress. Работает через рефлексию, поэтому модуль не зависит от версии DevExpress в клиенте.
    /// <code>
    /// private void PilotReport_ParametersRequestBeforeShow(object sender, ParametersRequestEventArgs e)
    /// {
    ///     // Динамический список типов заданий с предвыбором
    ///     ReportParams.SetLookUp(this, "parameter2",
    ///         Report.Context.GetTypes().Where(t =&gt; t.Name.Contains("task_")).Select(t =&gt; ReportParams.Item(t.Name, t.Title)),
    ///         selected: new[] { "task_approval", "task_approval_exp" });
    /// }
    /// </code>
    /// </summary>
    public static class ReportParams
    {
        /// <summary>Элемент списка значений: значение и подпись.</summary>
        public static KeyValuePair<object, string> Item(object value, string description) =>
            new KeyValuePair<object, string>(value, description);

        /// <summary>Значение параметра, приведённое к T (default, если пусто или другого типа).</summary>
        public static T Get<T>(object report, string name)
        {
            var value = GetValue(report, name);
            if (value is T typed)
                return typed;
            if (value == null || value is string s && s.Length == 0)
                return default;

            // Мультивыбор: DevExpress отдаёт object[] / string[] - приводим к нужному массиву.
            if (typeof(T).IsArray && value is IEnumerable items && !(value is string))
            {
                var elementType = typeof(T).GetElementType();
                var list = items.Cast<object>().Where(elementType.IsInstanceOfType).ToArray();
                var array = Array.CreateInstance(elementType, list.Length);
                Array.Copy(list, array, list.Length);
                return (T)(object)array;
            }

            try
            {
                return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }
            catch
            {
                return default;
            }
        }

        /// <summary>Значение параметра; если не заполнено - ReportException (Pilot покажет текст пользователю).</summary>
        public static T Require<T>(object report, string name, string message)
        {
            var value = Get<T>(report, name);
            var empty = value == null
                        || value is string s && string.IsNullOrWhiteSpace(s)
                        || value is Array a && a.Length == 0
                        || value is Guid g && g == Guid.Empty;
            if (empty)
                throw new ReportException(message);
            return value;
        }

        /// <summary>Сырое значение параметра или null.</summary>
        public static object GetValue(object report, string name) => GetProperty(FindParameter(report, name), "Value");

        /// <summary>Устанавливает значение параметра.</summary>
        public static void SetValue(object report, string name, object value)
        {
            var parameter = FindParameter(report, name) ?? throw new ArgumentException($"В отчёте нет параметра '{name}'");
            parameter.GetType().GetProperty("Value")?.SetValue(parameter, value);
        }

        /// <summary>
        /// Заполняет выпадающий список параметра (StaticListLookUpSettings) и, при необходимости, предвыбирает значения.
        /// Вызывать из PilotReport_ParametersRequestBeforeShow.
        /// </summary>
        public static void SetLookUp(object report, string name, IEnumerable<KeyValuePair<object, string>> items, IEnumerable<object> selected = null)
        {
            var parameter = FindParameter(report, name) ?? throw new ArgumentException($"В отчёте нет параметра '{name}'");
            var assembly = parameter.GetType().Assembly;
            var settingsType = assembly.GetType("DevExpress.XtraReports.Parameters.StaticListLookUpSettings", throwOnError: true);
            var valueType = assembly.GetType("DevExpress.XtraReports.Parameters.LookUpValue", throwOnError: true);

            var settings = Activator.CreateInstance(settingsType);
            var values = (IList)settingsType.GetProperty("LookUpValues").GetValue(settings);
            foreach (var item in items ?? Enumerable.Empty<KeyValuePair<object, string>>())
                values.Add(Activator.CreateInstance(valueType, item.Key, item.Value));

            parameter.GetType().GetProperty("LookUpSettings").SetValue(parameter, settings);

            if (selected == null)
                return;

            var isMulti = GetProperty(parameter, "MultiValue") as bool? == true;
            var selectedList = selected.ToList();
            var elementType = GetProperty(parameter, "Type") as Type ?? typeof(object);
            if (isMulti)
            {
                var array = Array.CreateInstance(elementType, selectedList.Count);
                for (var i = 0; i < selectedList.Count; i++)
                    array.SetValue(selectedList[i], i);
                SetValue(report, name, array);
            }
            else if (selectedList.Count > 0)
            {
                SetValue(report, name, selectedList[0]);
            }
        }

        /// <summary>Объект параметра DevExpress (Parameter) по имени или null.</summary>
        public static object FindParameter(object report, string name)
        {
            if (!(GetProperty(report, "Parameters") is IEnumerable parameters))
                return null;
            return parameters.Cast<object>().FirstOrDefault(p => GetProperty(p, "Name") as string == name);
        }

        private static object GetProperty(object target, string name)
        {
            if (target == null)
                return null;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property?.GetIndexParameters().Length == 0 ? property.GetValue(target) : null;
        }
    }
}
