# CashMovements

## Manual testing

- **Visual Studio:** pick the **Server + Client** launch profile and press F5.
- **Command line:** `.\test.ps1` from this folder.

Either way the local database is set up automatically. See [CashMovements.Client/README.md](CashMovements.Client/README.md).

## Automated tests

```powershell
dotnet test CashMovements.slnx
```

Repository and end-to-end tests need SQL Server: they use the local default instance, or the server in
`CASHMOVEMENTS_TEST_SQLSERVER` (a connection string without a database). Each run creates and drops its own database.
