# SimpleBank

A full banking web app built with **ASP.NET Core MVC (.NET 10)**, **EF Core + SQLite**, and **ASP.NET Core Identity**. It does what a mobile banking app does: multiple users, several accounts each, deposits and withdrawals, transfers, sending money to other people by handle, money requests, bill pay, paginated transaction history, a dashboard with charts, an admin console, and a small JSON API.

> A class project. No real money moves, and there are no real payment methods.

## Run it

```bash
cd bank
dotnet run --launch-profile http     # http://localhost:5080
```

On first run the app applies the **EF Core migrations** and **seeds** demo data (six users, about six months of history). To reset, stop the app and delete `simplebank.db`.

To run the tests from the solution folder:

```bash
dotnet test
```

> **OneDrive note:** this folder lives in OneDrive. If you see "database is locked" errors or `-conflict` copies of files, pause OneDrive syncing (or copy the project somewhere outside OneDrive) before running the demo.

### Demo logins (password for all: `Demo123!`)

| Email | Who | Notes |
|---|---|---|
| `demo@simplebank.local` | Jordan Rivera (`@jordan`) | Main demo customer: checking, savings, and a "Japan Trip" goal, plus pending requests |
| `alice@simplebank.local` | Alice Chen (`@alice`) | Use her to receive money and send requests |
| `admin@simplebank.local` | Morgan Blake | Admin console |

`bob`, `carol` and `dave` `@simplebank.local` also exist. The login page has one-click buttons to fill these in during development.

## Features

**Customers**
- Register, sign in and sign out (custom pages on Identity). Lockout after 5 failed attempts, plus rate-limited sign-in.
- Dashboard: a claret total-balance panel (with a split bar by account and labeled actions), accounts as rows, this month's money in/out, a balance-over-time chart (30d/90d/6m), a spending donut, a 6-month in/out bar chart, recent activity and requests waiting on you.
- Accounts: open Checking, Savings or **Goal** accounts (with a target and a progress ring); rename, set primary, close (balance must be $0). Account numbers are masked, with reveal and copy.
- Money: **Deposit**, **Withdraw**, **Transfer** between your accounts, **Send** to another user by `@handle` or email (live search), each as a details → review → receipt flow.
- **Requests**: ask someone for money; they pay (one tap) or decline; you can cancel.
- **Bill pay**: saved payees, pay from any account.
- **Activity**: every transaction with server-side **pagination** (10/25/50), filters (account, type, category, direction, date range), text search and sorting. Results update in place, and each row opens a detail drawer.
- Notifications: a bell with an unread badge, polled through the API. A toast appears and balances update live when someone pays you.
- Profile: name, handle, theme (light/dark/system), change password.

**Admin** (`/Admin`)
- KPIs and a 30-day volume chart. Customer search, freeze/unfreeze accounts, lock/unlock sign-in (signs the user out immediately), and post a balance adjustment with a required reason.
- Bank-wide ledger and an **audit log** (sign-ins, registrations, password changes, every admin action).

**Design**
- Custom design system ("Claret") with no CSS framework, laid out like a national bank's site: plain header, a deep claret band with a sign-in box on the landing page, account rows with "Available" balances, a security section and a full footer.
- **Nothing moves unless you act.** No count-ups, page transitions, chart entrance animations or decorative motion. Only hover and press feedback, drawers that slide in when opened, toasts and loading spinners.
- **Every color has one meaning.** Claret means "you can click it"; slate, eucalyptus and plum mark Checking, Savings and Goal accounts; pine green with "+" is money in; dark with "−" is money out; bronze is pending or frozen; red is an error or something that can't be undone. Category colors were checked for color-blind separation in both themes.
- One heritage detail: a still guilloché rosette on the balance panel, the landing band and the sign-in panel.
- Self-hosted Source Serif 4 (headings, balances) and Manrope (everything else). Light and warm-charcoal dark themes with no flash on load.
- Responsive: desktop sidebar, tablet icon rail with labels, and a phone tab bar with a labeled "Move" sheet. Admin tables become stacked rows on phones.
- Clarity: buttons name their action ("Review transfer", "Pay $40.00"), icon-only buttons have tooltips, and actions that can't be undone (close account, decline, freeze, lock) ask in an in-page confirm dialog.

## API

Interactive docs: **http://localhost:5080/scalar** (OpenAPI document at `/openapi/v1.json`, Development only).

The API uses the same sign-in cookie as the website. Unauthenticated calls get `401` JSON, not a redirect. `POST` requests need an antiforgery token:

1. Sign in to the site in the same browser.
2. `GET /api/v1/antiforgery/token` and copy `token`.
3. Send it as the `RequestVerificationToken` header on POSTs. (In Scalar, add it under Headers.)

| Method | Endpoint | Purpose |
|---|---|---|
| GET | `/api/v1/accounts`, `/api/v1/accounts/{id}` | Your accounts |
| GET | `/api/v1/transactions?accountId&type&category&flow&from&to&q&sort&page&pageSize` | Paged search |
| POST | `/api/v1/deposits`, `/api/v1/withdrawals`, `/api/v1/transfers`, `/api/v1/payments/send` | Move money (optional `idempotencyKey`) |
| GET | `/api/v1/users/lookup?q=` | Find people to pay (rate-limited) |
| GET/POST | `/api/v1/notifications`, `/summary`, `/{id}/read`, `/read-all` | Notifications |
| GET | `/api/v1/insights/balance-history`, `/spending`, `/cashflow` | Chart data |
| GET | `/api/v1/admin/stats` | Admin KPIs (admin only) |

Business-rule failures return `400` with a `ProblemDetails` body (for example `"Insufficient funds. Everyday Checking has $40.00 available."`).

## How it's built

```
bank/                       solution folder (SimpleBank.slnx)
├─ bank/                    web project (SimpleBankMvc)
│  ├─ Models/               entities: ApplicationUser, BankAccount, Transaction, MoneyRequest, Notification, Payee, AuditLog, ProcessedRequest
│  ├─ Data/                 ApplicationDbContext, money converter, DbInitializer (migrate + seed)
│  ├─ Migrations/           InitialCreate → AddAccountGoals → AddAuditLog
│  ├─ Services/             all business rules (BankingService, MoneyRequestService, InsightsService, AdminService, …)
│  ├─ Controllers/          MVC pages; Controllers/Api/ = JSON API
│  ├─ Areas/Admin/          admin console
│  ├─ Views/, wwwroot/      Razor views, CSS design system, vanilla JS modules, Chart.js
└─ SimpleBankMvc.Tests/     xUnit tests on a real in-memory SQLite database
```

- **Ledger.** Balances change in exactly one place (`LedgerPoster`), which also writes the ledger line with `BalanceAfter`. Transfers and person-to-person payments write two lines that share a reference and net to zero.
- **Atomicity.** Every money operation runs in a database transaction that re-reads the accounts and checks ownership, frozen status and funds. On failure it rolls back and clears the change tracker.
- **Double-submit protection.** Money forms carry an idempotency key stored in `ProcessedRequests` inside the same transaction, so a replayed POST is rejected.
- **Money in SQLite.** SQLite can't sort or compare `decimal`, so every money column is stored as integer cents through a value converter. Filters and `ORDER BY Amount` then run in SQL; sums run in memory on already-narrowed queries.
- **Security.** Every query is scoped to the signed-in user. Antiforgery is validated globally (forms and API). Identity lockout, rate limiting on sign-in and user lookup, and the security stamp is re-validated on every request so locked users are signed out at once.
