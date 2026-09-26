/*
Support script template (T03). Every change is audited by the database; this template makes it carry a ticket and a reason.

  1. Copy this file, name it after the ticket (e.g. INC-1234.sql) and replace the $(...) placeholders
     — or run it with sqlcmd variables:  sqlcmd ... -v Ticket="INC-1234" Reason="..." NodeId=42 ContentJson="{...}"
  2. Edit ONLY the source of truth (app.NodeContent.ContentJson); the API re-renders the derived columns
     (ContentHtml, PlainText, ContentHash) automatically (T09 rule 8).
  3. Run it as a member of the support_writer role.

Changes are recorded with Source = 'Script', your database login, the ticket and the reason — also when they touch a
signed version (the version is then flagged "modified after signing").
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- sqlcmd connects with QUOTED_IDENTIFIER OFF; app tables have filtered/computed-column indexes that need both options ON
-- (otherwise: Msg 1934 "UPDATE failed because the following SET options have incorrect settings"). They must be set in a
-- batch of their own: a statement is compiled with the options its batch started with.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

EXEC audit.usp_SetSupportContext @Ticket = N'$(Ticket)', @Reason = N'$(Reason)';

BEGIN TRANSACTION;

    UPDATE app.NodeContent
    SET ContentJson = N'$(ContentJson)'
    WHERE NodeId = $(NodeId);

    IF @@ROWCOUNT <> 1
        THROW 50010, N'Expected to change exactly one row — nothing was changed.', 1;

COMMIT TRANSACTION;
