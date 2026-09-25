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
