# AGENTS.md

Guidance for AI coding agents (Claude Code, Gemini in Firebase Studio, and others) working on this repo.
Read this whole file before changing anything. Two different assistants work on this project, so these
rules are what keeps the work consistent.

## What this is

**SimpleBank** is a class project for a server-side web development course: a full banking web app built with
**ASP.NET Core MVC (.NET 10)**, **EF Core + SQLite** and **ASP.NET Core Identity**, with vanilla CSS and JavaScript
(no CSS framework, no front-end framework). No real money moves. See `bank/README.md` for features and the API.

```
bank/                       solution folder (SimpleBank.slnx)  ← repo root
├─ AGENTS.md                this file
├─ bank/                    web project (SimpleBankMvc)
│  ├─ Controllers/, Areas/Admin/, Services/, Models/, Data/, Migrations/
│  ├─ Views/                Razor views (customer pages, shared partials, layouts)
│  ├─ Infrastructure/Ui.cs  view helpers: icons, labels, AccountTone(), Rosette()
│  └─ wwwroot/              css/app.css (design system), css/pages.css (page styles), js/*.js, fonts/
└─ SimpleBankMvc.Tests/     xUnit tests (services only)
```

## Run, build, test

```bash
cd bank
dotnet run --launch-profile http     # http://localhost:5080
dotnet build                         # Razor views compile here; a view typo fails the build
cd .. && dotnet test                 # from the repo root; all tests must pass
```

- Requires the **.NET 10 SDK**. In a cloud workspace (Firebase Studio, Codespaces), make sure it is installed before running.
- First run migrates and seeds `bank/simplebank.db`. Delete that file to reset. `*.db` files are git-ignored; never commit them.
- Demo logins (password `Demo123!`): `demo@simplebank.local` (customer), `admin@simplebank.local` (admin console).
- Stop a running server before `dotnet build` / `dotnet test` on Windows, or the build output stays locked.

## Design rules (do not break these)

The teacher called an earlier version "casino-esque" because of its **animations and motion**, and asked for
something closer to a **national bank's website**. The current design ("Claret") was approved by the student.
Keep to it.

### 1. Nothing moves unless the user acts
- **Allowed:** hover/press color changes (120–160ms), buttons moving down 1px when pressed, drawers/sheets sliding
  in when opened, toast fade, the spinner inside a clicked button.
- **Not allowed:** count-up numbers, entrance/scroll animations, page transitions (View Transitions API), chart
  entrance animation (Chart.js uses `animation: false`), shimmer, ripples, springy or bouncy easing, spinning or
  floating decoration, circular theme reveals, anything that animates on page load.
- Check: `grep -n "@keyframes\|animation:" bank/wwwroot/css/*.css` should only list `spin`, `toast-in` and `toast-out`.

### 2. Every color has one meaning
Tokens live at the top of `bank/wwwroot/css/app.css` (light in `:root`, dark in `[data-theme="dark"]`). Use the
tokens; never hard-code new colors in views.

| Token | Light / dark | Means |
|---|---|---|
| `--primary` (claret) | `#7a2338` / `#f0a3b4` | Clickable: main buttons, links, active nav. Nothing else. |
| `--checking` (slate) | `#4d6380` / `#9fb4d2` | Checking account |
| `--savings` (eucalyptus) | `#4a766e` / `#88c0b4` | Savings account |
| `--goal` (plum) | `#7a4a78` / `#d2a3ce` | Goal account |
| `--positive` (pine) | `#3b7348` / `#8cc79c` | Money in, always shown with "+" |
| `--ink` | `#17202e` / `#efe9ec` | Money out, shown with a true minus "−", never red |
| `--warning` (bronze) | `#8f6424` / `#d9ae6a` | Pending or frozen |
| `--negative` (red) | `#b3261e` / `#f28b82` | Errors and actions that can't be undone |

- The companion colors are **muted on purpose** so they sit quietly beside claret. Don't brighten them.
- Account colors come from `Ui.AccountTone(type)`, which returns `tone-checking` / `tone-savings` / `tone-goal`. Those
  classes set `--acct`, `--acct-soft` and `--acct-on`. Use them for anything account-specific.
- Category colors (`.cat-Bills`, `.cat-Dining`, …) were validated for color-blind separation. Don't change them casually.
- Color never carries meaning alone: always pair it with a word, a sign (+/−) or an icon.
- **Claret vs. red:** main buttons are filled claret (`.btn-primary`). Destructive buttons are *outlined* red
  (`.btn-danger`) with an icon, so the two never look alike.
- Dark mode is a **warm charcoal** (`--bg: #16121a`), not blue-gray. Tune it, don't invert it.

### 3. Type, shape and the rosette
- **Source Serif 4** for h1/h2 and big money figures; **Manrope** for everything else. Both self-hosted in `wwwroot/fonts/`.
  Don't add fonts or load them from a CDN.
- Numbers use tabular figures (`.num`), cents are lighter (`.cents`), negatives use the real minus sign "−".
- Radii: 6px controls, 10px cards, 14px big panels. Hairline borders; shadows only on floating layers.
- The **guilloché rosette** (`Ui.Rosette()`, `.rosette`) appears in exactly three places: the dashboard balance panel,
  the landing hero band and the sign-in side panel. It is **always still**. Don't add it elsewhere or animate it.

### 4. Every button and menu is obvious
- Button text names the action and its object: "Review transfer", "Pay $40.00", "Open a savings account".
  Not "Submit", "OK" or "Go".
- Link text says where it goes: "View all activity", not "See all".
- Icon-only buttons need an `aria-label` **and** a `data-tip` tooltip that says what happens.
- Destructive actions use `data-confirm="consequence"` plus optional `data-confirm-title` and `data-confirm-ok`
  (the Confirm button's label). `ui.js` shows the in-page `<dialog id="confirm-dialog">`. Never use `window.confirm()`.
  The dialog's own buttons use `data-dialog-ok` / `data-dialog-cancel`; don't reuse those names elsewhere.
- Forms: label above every field, helper text in `.hint`, "(optional)" in `.optional`, errors in `.field-error`.
- Empty states always offer the next action as a button.

### 5. Words
- Write original copy. **Never copy another company's slogan, motto, logo or branding**, banks included.
  Borrow layout patterns only.
- Describe only features that really exist. Plain, short sentences in sentence case. No em-dash asides,
  no middle-dot "A · B" strings, no ALL-CAPS labels, no "→" appended to links.

### 6. Responsive
- Breakpoints: ≤1100px tablet icon rail with labels; ≤760px phone tab bar plus the labeled "Move" sheet.
- Every page must work at 375px wide with **no horizontal scrolling**. Hidden absolutely positioned things
  (tooltips, `.sr-only` inputs) must not widen the page; give their parent `position: relative`.
- Admin tables use `.table-stack` with `data-label` on each `<td>` so they become stacked rows on phones.

## Code conventions
- **Keep JS hooks stable.** Views are wired to JS through `data-*` attributes and ids (`data-tx`, `data-drawer-open`,
  `data-results-host`, `data-live-total`, `data-account-id`, `data-balance`, …). Don't rename them without
  updating `wwwroot/js/*.js`.
- Shared UI belongs in `app.css`; page-specific styles in `pages.css`. Reuse existing components (`.btn`, `.card`,
  `.chip`, `.acct-row`, `.tx`, `.drawer`, `.panel-head`) before writing new CSS.
- Business rules live in `bank/Services/`. Views and controllers don't move money themselves.
- Don't change controllers, services, models or migrations for a visual task.

## Working between two assistants
- Work on a **branch** per task (`claude/<topic>` or `gemini/<topic>`), not directly on `master`, and merge through a
  pull request. Pull `master` before starting.
- Keep commits small, with a clear message about what changed and why.
- Before finishing: `dotnet build` and `dotnet test` must pass, and the page should be checked at desktop and phone
  width in light and dark mode.
- If you change a design rule, update this file in the same commit so the other assistant knows.
