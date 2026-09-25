-- Database roles (T02). Audit-specific grants are added with the audit schema objects (T03).
CREATE ROLE [app_api] AUTHORIZATION [dbo];
GO
CREATE ROLE [support_writer] AUTHORIZATION [dbo];
GO
CREATE ROLE [readonly] AUTHORIZATION [dbo];
GO
-- Reads the ledger and module definitions (T21 §4); app_api is a member (post-deployment script Security.RoleMembership.sql,
-- because deployments exclude role memberships so they keep the environment's users).
CREATE ROLE [ledger_reader] AUTHORIZATION [dbo];
