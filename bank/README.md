# SimpleBankMvc

Educational ASP.NET Core MVC banking demo using Individual Accounts, ASP.NET Core Identity, EF Core, and SQLite.

## Run

```bash
dotnet restore
dotnet run
```

Open the HTTPS URL shown in the terminal.

Demo login: `demo@simplebank.local` / `Demo123!`

The SQLite database `simplebank.db` is created automatically on first run. Delete it to reset the demo.

## Features

- Individual user registration, login, logout, password/profile UI
- Multiple accounts per authenticated user
- Ownership checks in every account query
- Dashboard and transaction history
- Atomic transfers between a user's accounts
- Payee management and simulated bill payments
- Anti-forgery validation and Identity lockout/cookie configuration

## Important

This is a learning project, not production banking software. It has no real payment rails, ACH processing, encryption/key management strategy, audit/WORM store, fraud controls, regulatory reporting, or production-grade ledger. `EnsureCreated` is used for a zero-friction demo; switch to EF Core migrations for maintained deployments.
