-- Built-in, Word-compatible styles (docs/content-format.md §2). Sizes in half-points, spacing in twips (240 = single line).
DECLARE @Styles TABLE ([StyleId] VARCHAR (50) NOT NULL PRIMARY KEY, [Name] NVARCHAR (100) NOT NULL, [Kind] TINYINT NOT NULL,
                       [BasedOnStyleId] VARCHAR (50) NULL, [PropertiesJson] NVARCHAR (MAX) NOT NULL);

INSERT INTO @Styles ([StyleId], [Name], [Kind], [BasedOnStyleId], [PropertiesJson]) VALUES
    ('Normal',        N'Normal',         1, NULL,       N'{"fontFamily":"Aptos","fontSize":22,"color":"#000000","spacingAfter":160,"lineSpacing":259,"lineRule":"auto"}'),
    ('Heading1',      N'Heading 1',      1, 'Normal',   N'{"fontFamily":"Aptos Display","fontSize":40,"color":"#0F4761","spacingBefore":360,"spacingAfter":80,"keepWithNext":true}'),
    ('Heading2',      N'Heading 2',      1, 'Normal',   N'{"fontFamily":"Aptos Display","fontSize":32,"color":"#0F4761","spacingBefore":160,"spacingAfter":80,"keepWithNext":true}'),
    ('Heading3',      N'Heading 3',      1, 'Normal',   N'{"fontFamily":"Aptos","fontSize":28,"color":"#0F4761","spacingBefore":160,"spacingAfter":80,"keepWithNext":true}'),
    ('Heading4',      N'Heading 4',      1, 'Normal',   N'{"fontFamily":"Aptos","fontSize":22,"italic":true,"color":"#0F4761","spacingBefore":80,"spacingAfter":40,"keepWithNext":true}'),
    ('Heading5',      N'Heading 5',      1, 'Normal',   N'{"fontFamily":"Aptos","fontSize":22,"color":"#0F4761","spacingBefore":80,"spacingAfter":40,"keepWithNext":true}'),
    ('Heading6',      N'Heading 6',      1, 'Normal',   N'{"fontFamily":"Aptos","fontSize":22,"italic":true,"color":"#595959","spacingBefore":40,"spacingAfter":0,"keepWithNext":true}'),
    ('Title',         N'Title',          1, 'Normal',   N'{"fontFamily":"Aptos Display","fontSize":56,"spacingAfter":80,"lineSpacing":240,"lineRule":"auto"}'),
    ('Subtitle',      N'Subtitle',       1, 'Normal',   N'{"fontFamily":"Aptos","fontSize":28,"color":"#595959","spacingAfter":160}'),
    ('Quote',         N'Quote',          1, 'Normal',   N'{"italic":true,"color":"#404040","align":"center","spacingBefore":160,"spacingAfter":160}'),
    ('ListParagraph', N'List Paragraph', 1, 'Normal',   N'{"indentLeft":720,"contextualSpacing":true}'),
    ('Caption',       N'Caption',        1, 'Normal',   N'{"fontSize":18,"italic":true,"color":"#0E2841","spacingAfter":200,"lineSpacing":240,"lineRule":"auto"}'),
    ('TableGrid',     N'Table Grid',     3, NULL,       N'{"borders":{"top":{"style":"single","size":4,"color":"#000000"},"left":{"style":"single","size":4,"color":"#000000"},"bottom":{"style":"single","size":4,"color":"#000000"},"right":{"style":"single","size":4,"color":"#000000"},"insideH":{"style":"single","size":4,"color":"#000000"},"insideV":{"style":"single","size":4,"color":"#000000"}}}'),
    ('Strong',        N'Strong',         2, NULL,       N'{"bold":true}'),
    ('Emphasis',      N'Emphasis',       2, NULL,       N'{"italic":true}');

-- Insert missing built-in styles first without inheritance (the self-reference is set in the second step).
INSERT INTO [app].[ContentStyle] ([StyleId], [Name], [Kind], [BasedOnStyleId], [PropertiesJson], [IsBuiltIn], [IsActive])
SELECT [s].[StyleId], [s].[Name], [s].[Kind], NULL, [s].[PropertiesJson], 1, 1
FROM @Styles AS [s]
WHERE NOT EXISTS (SELECT 1 FROM [app].[ContentStyle] AS [c] WHERE [c].[StyleId] = [s].[StyleId]);

-- Link inheritance only for styles inserted by this seed that still have no base style.
UPDATE [c]
SET [BasedOnStyleId] = [s].[BasedOnStyleId]
FROM [app].[ContentStyle] AS [c]
JOIN @Styles AS [s] ON [s].[StyleId] = [c].[StyleId]
WHERE [c].[IsBuiltIn] = 1 AND [c].[BasedOnStyleId] IS NULL AND [s].[BasedOnStyleId] IS NOT NULL
  AND [c].[PropertiesJson] = [s].[PropertiesJson];
