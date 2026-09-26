namespace DocHub.Domain.Entities;

public enum VersionStatus : byte
{
    Draft = 1,
    Signed = 2,
    Deleted = 3,
}

public enum DocumentRole : byte
{
    Editor = 1,
    Approver = 2,
}

public enum ContentStyleKind : byte
{
    Paragraph = 1,
    Character = 2,
    Table = 3,
}

/// <summary>PDF export job state (T20); stored as <c>app.ExportJob.Status</c>.</summary>
public enum ExportStatus : byte
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}
