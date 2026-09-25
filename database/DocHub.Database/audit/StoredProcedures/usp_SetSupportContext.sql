/*
Support scripts call this first so their changes carry a ticket and a reason (FR-H3):
    EXEC audit.usp_SetSupportContext @Ticket = N'INC-1234', @Reason = N'...', @ActingUserId = NULL;
Changes are recorded as Source = 'Script' with ORIGINAL_LOGIN(); @ActingUserId records on whose behalf the change was made.
*/
CREATE PROCEDURE [audit].[usp_SetSupportContext]
    @Ticket       NVARCHAR (50),
    @Reason       NVARCHAR (500),
    @ActingUserId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NULLIF(LTRIM(RTRIM(@Ticket)), N'') IS NULL
        THROW 50001, N'A ticket reference is required.', 1;

    IF NULLIF(LTRIM(RTRIM(@Reason)), N'') IS NULL
        THROW 50002, N'A reason is required.', 1;

    IF @ActingUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [app].[User] WHERE [Id] = @ActingUserId)
        THROW 50003, N'The acting user does not exist.', 1;

    DECLARE @TicketValue NVARCHAR (50) = LTRIM(RTRIM(@Ticket));
    DECLARE @ReasonValue NVARCHAR (500) = LTRIM(RTRIM(@Reason));

    EXEC sys.sp_set_session_context @key = N'Ticket', @value = @TicketValue;
    EXEC sys.sp_set_session_context @key = N'Reason', @value = @ReasonValue;
    EXEC sys.sp_set_session_context @key = N'UserId', @value = @ActingUserId;
END;
