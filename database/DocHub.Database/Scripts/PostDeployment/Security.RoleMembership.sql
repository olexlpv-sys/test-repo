-- Role-in-role membership owned by the project (deployments exclude role memberships to keep the environment's users).
IF IS_ROLEMEMBER(N'ledger_reader', N'app_api') = 0
    ALTER ROLE [ledger_reader] ADD MEMBER [app_api];
