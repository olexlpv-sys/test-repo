-- <post-deployment> Drift checks ignore the deploy script from this line on (idempotent data and membership upkeep).
/*
Post-deployment seed data. Idempotent: every deployment may run it again.
  - Users are seed-managed: upserted from this list (users not in the list, e.g. generated load-test users, are kept).
  - Built-in content styles are inserted when missing; existing styles are never overwritten (admins may edit them).
  - Role memberships between the project's own roles are added when missing.
  - Node types and root folders are initial data: inserted only into an empty table (admins own them afterwards).
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

:r .\Security.RoleMembership.sql
:r .\Seed.Users.sql
:r .\Seed.ContentStyles.sql
:r .\Seed.NodeTypes.sql
:r .\Seed.Folders.sql
