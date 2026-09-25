-- Database roles (T02). Audit-specific grants are added with the audit schema objects (T03).
CREATE ROLE [app_api] AUTHORIZATION [dbo];
GO
CREATE ROLE [support_writer] AUTHORIZATION [dbo];
GO
CREATE ROLE [readonly] AUTHORIZATION [dbo];
