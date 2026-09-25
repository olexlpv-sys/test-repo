IF NOT EXISTS (SELECT 1 FROM [app].[NodeType])
BEGIN
    SET IDENTITY_INSERT [app].[NodeType] ON;

    INSERT INTO [app].[NodeType] ([Id], [Code], [Name], [Description], [SortOrder], [IsActive]) VALUES
        (1, 'CHAPTER',    N'Chapter',    N'Top-level division of a document', 10, 1),
        (2, 'SECTION',    N'Section',    N'Division of a chapter',            20, 1),
        (3, 'SUBSECTION', N'Subsection', N'Division of a section',            30, 1),
        (4, 'PARAGRAPH',  N'Paragraph',  N'Numbered paragraph or clause',     40, 1),
        (5, 'APPENDIX',   N'Appendix',   N'Appendix or annex',                50, 1);

    SET IDENTITY_INSERT [app].[NodeType] OFF;
END;
