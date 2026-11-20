using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ascon.Pilot.SDK;

namespace Reports.Manager
{
    /// <summary>Узел дерева «Загрузить отчёт»: папка отчётов или отчёт.</summary>
    internal sealed class ReportNode
    {
        public ReportNode(IDataObject obj, bool isFolder)
        {
            Object = obj;
            IsFolder = isFolder;
            Title = isFolder ? obj.DisplayName : PilotReports.ReportName(obj);
        }

        public IDataObject Object { get; set; }
        public Guid Id => Object.Id;
        public bool IsFolder { get; }
        public string Title { get; }
        public List<ReportNode> Children { get; } = new List<ReportNode>();
        public bool IsExpanded { get; set; }
        public string Icon => IsFolder ? "" : "";

        /// <summary>Копия папки с другим набором детей - для дерева, отфильтрованного по поиску.</summary>
        public ReportNode WithChildren(IEnumerable<ReportNode> children)
        {
            var copy = new ReportNode(Object, IsFolder) { IsExpanded = true };
            copy.Children.AddRange(children);
            return copy;
        }
    }

    /// <summary>Отчёты базы Pilot через SDK: дерево папок, текущий .repx, новая версия с причиной.</summary>
    internal static class PilotReports
    {
        public const string ReportTypeName = "Report_6088AF81-061E-456E-9225-CF65B7B25368";
        public const string FolderTypeName = "Report_Folder_F2CC6F1D-70E1-4E9B-B32F-BEB3E991318F";
        public const string ReportNameAttribute = "Report_name_D745D627-E7CD-4E3A-B30E-F9EDC1A09D77";
        private const string RepxExtension = ".repx";
        private static readonly Guid StandardRootId = new Guid("00000001-0001-0001-0001-000000000001");

        public static bool IsReport(IDataObject obj) =>
            string.Equals(obj?.Type?.Name, ReportTypeName, StringComparison.OrdinalIgnoreCase);

        public static bool IsFolder(IDataObject obj) =>
            string.Equals(obj?.Type?.Name, FolderTypeName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Имя отчёта: атрибут «Наименование», иначе заголовок объекта.</summary>
        public static string ReportName(IDataObject report)
        {
            if (report.Attributes != null && report.Attributes.TryGetValue(ReportNameAttribute, out var value) &&
                value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                return value.ToString().Trim();
            return report.DisplayName;
        }

        /// <summary>
        /// Дерево отчётов: корневые папки отчётов (папки, чей родитель не папка отчётов) и всё внутри них.
        /// Удалённые и объекты в корзине не показываются.
        /// </summary>
        public static List<ReportNode> LoadTree()
        {
            var roots = FindRootFolders().Select(f => new ReportNode(f, true) { IsExpanded = true }).ToList();

            // Обход вниз по уровням: один запрос SDK на уровень, а не на каждую папку.
            var level = roots;
            while (level.Count > 0)
            {
                var childIds = level.SelectMany(n => n.Object.Children ?? Enumerable.Empty<Guid>()).Distinct().ToList();
                if (childIds.Count == 0)
                    break;
                var loaded = PilotApi.LoadObjects(childIds).Where(IsAlive).ToDictionary(o => o.Id);

                var next = new List<ReportNode>();
                foreach (var parent in level)
                {
                    foreach (var id in parent.Object.Children ?? Enumerable.Empty<Guid>())
                    {
                        if (!loaded.TryGetValue(id, out var child))
                            continue;
                        if (IsFolder(child))
                        {
                            var node = new ReportNode(child, true);
                            parent.Children.Add(node);
                            next.Add(node);
                        }
                        else if (IsReport(child))
                        {
                            parent.Children.Add(new ReportNode(child, false));
                        }
                    }
                    Sort(parent.Children);
                }
                level = next;
            }
            Sort(roots);
            Infrastructure.ModuleLog.Info($"Дерево отчётов: корневых папок {roots.Count}, отчётов {Count(roots)}.");
            return roots;
        }

        private static int Count(IEnumerable<ReportNode> nodes) => nodes.Sum(n => (n.IsFolder ? 0 : 1) + Count(n.Children));

        /// <summary>
        /// Корневые папки отчётов. Папка отчётов - служебный объект, дочерний к Global Root (родителю
        /// стандартного корня базы), а не к корню, который видит пользователь; поиск SDK её обычно не находит.
        /// </summary>
        private static List<IDataObject> FindRootFolders()
        {
            var standardRoot = PilotApi.LoadObject(StandardRootId);
            if (standardRoot != null && standardRoot.ParentId != Guid.Empty)
            {
                var globalRoot = PilotApi.LoadObject(standardRoot.ParentId);
                var children = globalRoot?.Children?.ToList() ?? new List<Guid>();
                var folders = PilotApi.LoadObjects(children).Where(o => IsFolder(o) && IsAlive(o)).ToList();
                if (folders.Count > 0)
                    return folders;
                Infrastructure.ModuleLog.Warn($"Папка отчётов не найдена среди {children.Count} детей Global Root {standardRoot.ParentId}, пробую поиск.");
            }
            else
            {
                Infrastructure.ModuleLog.Warn("Не удалось загрузить стандартный корень базы, пробую поиск папки отчётов.");
            }

            var folderType = PilotApi.GetType(FolderTypeName)
                             ?? throw new InvalidOperationException("В базе нет типа папки отчётов (Report_Folder).");
            var found = PilotApi.LoadObjects(PilotApi.SearchIds(b => b.Must(ObjectFields.TypeId.Be(folderType.Id))))
                .Where(IsAlive).ToList();
            var known = new HashSet<Guid>(found.Select(f => f.Id));
            return found.Where(f => !known.Contains(f.ParentId)).ToList();
        }

        private static void Sort(List<ReportNode> nodes) =>
            nodes.Sort((a, b) => a.IsFolder != b.IsFolder
                ? (a.IsFolder ? -1 : 1)
                : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));

        private static bool IsAlive(IDataObject obj) =>
            obj != null && obj.ObjectStateInfo?.State != ObjectState.DeletedPermanently && obj.ObjectStateInfo?.State != ObjectState.InRecycleBin;

        /// <summary>Файл .repx актуальной версии отчёта; null - файла нет.</summary>
        public static IFile CurrentRepx(IDataObject report) =>
            (report.ActualFileSnapshot?.Files ?? report.Files ?? Enumerable.Empty<IFile>())
            .FirstOrDefault(f => f.Name.EndsWith(RepxExtension, StringComparison.OrdinalIgnoreCase));

        /// <summary>Содержимое текущего .repx отчёта; null - у отчёта нет файла.</summary>
        public static byte[] ReadCurrent(IDataObject report)
        {
            var file = CurrentRepx(report);
            if (file == null)
                return null;
            using (var source = PilotApi.Files.OpenRead(file))
            using (var buffer = new MemoryStream())
            {
                source.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        /// <summary>
        /// Новая версия отчёта: новый снапшот файлов с причиной (видна в истории версий отчёта в Pilot)
        /// и .repx в нём. Имя файла сохраняется прежним.
        /// </summary>
        public static void Update(IDataObject report, byte[] content, string reason, PendingSave intent, string fileName = null)
        {
            fileName = fileName ?? CurrentRepx(report)?.Name ?? "report.repx";
            var now = DateTime.UtcNow;
            PendingSaves.Expect(report.Id, intent);
            try
            {
                using (var stream = new MemoryStream(content))
                {
                    PilotApi.Modifier.Edit(report)
                        .CreateFileSnapshot(reason)
                        .AddFile(fileName, stream, now, now, now);
                    PilotApi.Modifier.Apply();
                }
            }
            catch
            {
                PendingSaves.Forget(report.Id);
                throw;
            }
        }
    }

    /// <summary>
    /// Что делать окну отправки в GitLab (<see cref="Publishing.ReportSaveHandler"/>), когда новую версию
    /// отчёта создал этот модуль, а не дизайнер.
    /// </summary>
    internal sealed class PendingSave
    {
        /// <summary>Показать окно отправки в GitLab (загрузка с диска) или нет (версия и так взята из GitLab).</summary>
        public bool OfferPublish { get; set; }

        /// <summary>Текст «Что изменилось» в окне отправки - причина, введённая при загрузке.</summary>
        public string Message { get; set; }

        internal DateTime Expires { get; set; }
    }

    internal static class PendingSaves
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<Guid, PendingSave> Items = new ConcurrentDictionary<Guid, PendingSave>();

        public static void Expect(Guid reportId, PendingSave intent)
        {
            intent.Expires = DateTime.UtcNow + Lifetime;
            Items[reportId] = intent;
        }

        public static void Forget(Guid reportId) => Items.TryRemove(reportId, out _);

        /// <summary>Намерение для этого отчёта; устаревшие (обработчик сохранения так и не вызвался) не учитываются.</summary>
        public static PendingSave Take(Guid reportId) =>
            Items.TryRemove(reportId, out var intent) && intent.Expires > DateTime.UtcNow ? intent : null;
    }
}
