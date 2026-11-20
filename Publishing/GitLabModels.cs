using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Reports.Publishing
{
    [DataContract]
    internal sealed class GitLabProject
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "path_with_namespace")] public string PathWithNamespace { get; set; }
        [DataMember(Name = "web_url")] public string WebUrl { get; set; }
        [DataMember(Name = "archived")] public bool Archived { get; set; }
    }

    [DataContract]
    internal sealed class GitLabTreeItem
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "type")] public string Type { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
    }

    [DataContract]
    internal sealed class GitLabCommit
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "short_id")] public string ShortId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "author_name")] public string AuthorName { get; set; }
        [DataMember(Name = "committed_date")] public string CommittedDate { get; set; }
        [DataMember(Name = "web_url")] public string WebUrl { get; set; }

        public DateTimeOffset? Date =>
            DateTimeOffset.TryParse(CommittedDate, out var d) ? d : (DateTimeOffset?)null;
    }

    [DataContract]
    internal sealed class GitLabUser
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "username")] public string Username { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "is_admin")] public bool IsAdmin { get; set; }
    }

    [DataContract]
    internal sealed class GitLabGroup
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "full_path")] public string FullPath { get; set; }

        /// <summary>Кто может создавать проекты: noone, owner, maintainer, developer, administrator.</summary>
        [DataMember(Name = "project_creation_level")] public string ProjectCreationLevel { get; set; }
    }

    [DataContract]
    internal sealed class GitLabMember
    {
        [DataMember(Name = "access_level")] public int AccessLevel { get; set; }
    }

    [DataContract]
    internal sealed class GitLabBranch
    {
        [DataMember(Name = "name")] public string Name { get; set; }
    }

    [DataContract]
    internal sealed class GitLabError
    {
        [DataMember(Name = "message")] public object Message { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
    }

    internal sealed class GitLabApiException : Exception
    {
        public GitLabApiException(int status, string message) : base(message) => Status = status;

        public int Status { get; }
    }

    /// <summary>Файл для коммита: путь, создать или обновить, содержимое байт в байт.</summary>
    internal sealed class CommitFile
    {
        public CommitFile(string path, bool create, byte[] content)
        {
            Path = path;
            Create = create;
            Content = content;
        }

        public string Path { get; }
        public bool Create { get; }
        public byte[] Content { get; }
    }

    /// <summary>Проект отчёта, найденный в группе, и файл отчёта в ветке.</summary>
    internal sealed class ReportRepository
    {
        public GitLabProject Project { get; set; }

        /// <summary>Папка - путь подгруппы относительно группы отчётов.</summary>
        public string Folder { get; set; }

        public Dictionary<string, string> RepxPathByBranch { get; } = new Dictionary<string, string>();
        public HashSet<string> Branches { get; } = new HashSet<string>();

        /// <summary>Ветки, в которых уже есть CHANGELOG.md.</summary>
        public HashSet<string> ChangelogBranches { get; } = new HashSet<string>();
    }
}
