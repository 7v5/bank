using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Data;

/// <summary>
/// Applies migrations, then (only when the database has no users) seeds roles, demo users and about
/// six months of realistic, deterministic history. Every balance change goes through
/// <see cref="LedgerPoster"/>, so seeded balances always reconcile with the ledger.
/// </summary>
public static class DbInitializer
{
    public const string DemoPassword = "Demo123!";
    public const string AdminRole = "Admin";
    public const string CustomerRole = "Customer";

    public record SeedLogin(string Email, string Name, string Role);

    /// <summary>Shown on the login page in Development so the demo is easy to drive.</summary>
    public static readonly SeedLogin[] DemoLogins =
    [
        new("demo@simplebank.local", "Jordan Rivera", "Customer"),
        new("alice@simplebank.local", "Alice Chen", "Customer"),
        new("admin@simplebank.local", "Morgan Blake", "Admin"),
    ];

    private record Persona(string Handle, string Name, string Color, decimal Paycheck, decimal Rent, string Employer, bool ExtraVault);

    private static readonly Persona[] Customers =
    [
        new("jordan", "Jordan Rivera", "#5b5bd6", 2350m, 1450m, "Northwind Labs", true),
        new("alice", "Alice Chen", "#e5484d", 2780m, 1720m, "Contoso Health", false),
        new("bob", "Bob Martinez", "#12a594", 1960m, 1180m, "Fabrikam Logistics", false),
        new("carol", "Carol Okafor", "#f76b15", 3120m, 1990m, "Litware Studio", true),
        new("dave", "Dave Patel", "#8e4ec6", 2140m, 1320m, "Tailspin Toys", false),
    ];

    private static readonly (TransactionCategory Cat, string[] Merchants, decimal Min, decimal Max, double PerWeek)[] Spending =
    [
        (TransactionCategory.Groceries, ["Fresh Market", "Green Grocer", "Corner Pantry", "Harvest Foods"], 18m, 145m, 2.2),
        (TransactionCategory.Dining, ["Bluebird Café", "Noodle House", "Taco Loco", "Slice Pizza Co.", "Sunrise Bagels"], 8m, 68m, 2.8),
        (TransactionCategory.Transport, ["Metro Transit", "QuickFuel", "RideShare"], 6m, 58m, 1.6),
        (TransactionCategory.Shopping, ["Urban Outfitters Co.", "Gadget Hub", "Home Goods", "Bookworm Books"], 15m, 210m, 0.9),
        (TransactionCategory.Entertainment, ["Cinema 8", "Arcade Bar", "Concert Hall", "Bowling Lanes"], 12m, 90m, 0.5),
    ];

    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        await SeedAsync(db, sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<RoleManager<IdentityRole>>(), sp.GetRequiredService<TimeProvider>());
    }

    public static async Task SeedAsync(ApplicationDbContext db, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles, TimeProvider clock)
    {
        foreach (var role in new[] { AdminRole, CustomerRole })
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));

        if (await db.Users.AnyAsync()) return;

        var now = clock.GetUtcNow().UtcDateTime;
        var random = new Random(2026);
        var poster = new LedgerPoster(db);

        // ---------- Users ----------
        var admin = await CreateUserAsync(users, "admin@simplebank.local", "admin", "Morgan Blake", "#b8912a", now.AddDays(-200), AdminRole);
        var people = new List<(Persona P, ApplicationUser U)>();
        foreach (var p in Customers)
        {
            var email = p.Handle == "jordan" ? "demo@simplebank.local" : $"{p.Handle}@simplebank.local";
            people.Add((p, await CreateUserAsync(users, email, p.Handle, p.Name, p.Color, now.AddDays(-190), CustomerRole)));
        }

        // ---------- Accounts ----------
        var start = now.Date.AddDays(-182);
        var accounts = new Dictionary<string, (BankAccount Checking, BankAccount Savings, BankAccount? Vault)>();
        foreach (var (p, u) in people)
        {
            var checking = NewAccount(u, "Everyday Checking", AccountType.Checking, true, start);
            var savings = NewAccount(u, "Emergency Savings", AccountType.Savings, false, start);
            var vault = p.ExtraVault ? NewAccount(u, p.Handle == "jordan" ? "Japan Trip" : "New Car Fund", AccountType.Savings, false, start.AddDays(20)) : null;
            foreach (var a in new[] { checking, savings, vault }.OfType<BankAccount>())
            {
                a.AccountNumber = await BankingService.NewAccountNumberAsync(db, random);
                db.BankAccounts.Add(a);
            }
            accounts[u.Id] = (checking, savings, vault);
        }
        await db.SaveChangesAsync();

        // ---------- Build a timeline of events, then post them in time order ----------
        var events = new List<(DateTime At, Action Post)>();
        void At(DateTime day, Action post) => events.Add((day.AddMinutes(random.Next(7 * 60, 22 * 60)), post));

        foreach (var (p, u) in people)
        {
            var (checking, savings, vault) = accounts[u.Id];
            var openChecking = Round(random.Next(900, 2600) + random.Next(0, 100) / 100m);
            var openSavings = Round(random.Next(2500, 9000));
            events.Add((start.AddHours(9), () => poster.Post(checking, openChecking, TransactionType.Deposit, TransactionCategory.Cash, "Opening deposit", Ref("OPN"), postedUtc: start.AddHours(9))));
            events.Add((start.AddHours(9.1), () => poster.Post(savings, openSavings, TransactionType.Deposit, TransactionCategory.Cash, "Opening deposit", Ref("OPN"), postedUtc: start.AddHours(9.1))));

            // Payroll every other Friday, with a savings sweep on the same day.
            var payday = start.AddDays(((int)DayOfWeek.Friday - (int)start.DayOfWeek + 7) % 7);
            var sweepTurn = 0;
            for (var d = payday; d < now; d = d.AddDays(14))
            {
                var at = d.AddHours(6);
                var pay = Round(p.Paycheck + random.Next(-40, 60));
                events.Add((at, () => poster.Post(checking, pay, TransactionType.Deposit, TransactionCategory.Income, $"Payroll — {p.Employer}", Ref("PAY"), p.Employer, at)));
                var sweepAt = at.AddHours(2);
                var target = vault is not null && sweepTurn++ % 2 == 1 ? vault : savings;
                var sweep = Round(random.Next(15, 45) * 10);
                events.Add((sweepAt, () => TryTransfer(poster, checking, target, sweep, "Auto-save", sweepAt)));
            }

            // Monthly bills.
            for (var m = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1); m < now; m = m.AddMonths(1))
            {
                var month = m;
                Bill(month.AddHours(10), checking, p.Rent, "Rent — Maple Court Apartments", "Maple Court Apartments", TransactionCategory.Bills);
                Bill(month.AddDays(4).AddHours(11), checking, Round(random.Next(58, 145) + random.Next(0, 99) / 100m), "City Power & Light", "City Power & Light", TransactionCategory.Bills);
                Bill(month.AddDays(7).AddHours(9), checking, 64.99m, "FiberNet Internet", "FiberNet Internet", TransactionCategory.Bills);
                Bill(month.AddDays(12).AddHours(9), checking, 45.00m, "Mobile plan — Skyline Wireless", "Skyline Wireless", TransactionCategory.Bills);
                Bill(month.AddDays(15).AddHours(3), checking, 15.99m, "StreamFlix subscription", "StreamFlix", TransactionCategory.Entertainment, TransactionType.Purchase);
                Bill(month.AddDays(18).AddHours(3), checking, 10.99m, "Tunes Premium", "Tunes", TransactionCategory.Entertainment, TransactionType.Purchase);
            }

            // Everyday card spending.
            for (var day = start.AddDays(1); day < now.Date.AddDays(1); day = day.AddDays(1))
            {
                foreach (var (cat, merchants, min, max, perWeek) in Spending)
                {
                    if (random.NextDouble() >= perWeek / 7.0) continue;
                    var merchant = merchants[random.Next(merchants.Length)];
                    var amount = Round(min + (decimal)random.NextDouble() * (max - min));
                    var when = day.AddMinutes(random.Next(7 * 60, 22 * 60));
                    if (when >= now) continue;
                    events.Add((when, () => TryPost(poster, checking, -amount, TransactionType.Purchase, cat, merchant, merchant, when)));
                }
                if (random.NextDouble() < 0.035)
                {
                    var cash = random.Next(2, 9) * 20m;
                    var when = day.AddHours(random.Next(9, 20));
                    if (when < now) events.Add((when, () => TryPost(poster, checking, -cash, TransactionType.Withdrawal, TransactionCategory.Cash, "ATM withdrawal", null, when)));
                }
            }

            void Bill(DateTime when, BankAccount from, decimal amount, string description, string payee, TransactionCategory cat, TransactionType type = TransactionType.BillPayment)
            {
                if (when >= now) return;
                events.Add((when, () => TryPost(poster, from, -amount, type, cat, description, payee, when)));
            }
        }

        // Person-to-person payments between friends.
        string[] notes = ["Pizza night", "Concert tickets", "Rent split", "Groceries", "Birthday gift", "Gas money", "Brunch", "Movie tickets", "Coffee", "Road trip"];
        for (var day = start.AddDays(3); day < now.Date; day = day.AddDays(random.Next(2, 6)))
        {
            var a = people[random.Next(people.Count)];
            var b = people[random.Next(people.Count)];
            if (a.U.Id == b.U.Id) continue;
            var amount = Round(random.Next(8, 90) + (random.NextDouble() < 0.5 ? 0 : 0.5m));
            var note = notes[random.Next(notes.Length)];
            var when = day.AddMinutes(random.Next(9 * 60, 23 * 60));
            var from = accounts[a.U.Id].Checking;
            var to = accounts[b.U.Id].Checking;
            events.Add((when, () =>
            {
                if (from.Balance < amount) return;
                var reference = Ref("P2P");
                poster.Post(from, -amount, TransactionType.P2PSent, TransactionCategory.People, note, reference, b.U.FullName, when);
                poster.Post(to, amount, TransactionType.P2PReceived, TransactionCategory.People, note, reference, a.U.FullName, when);
            }));
        }

        foreach (var e in events.OrderBy(e => e.At)) e.Post();
        await db.SaveChangesAsync();

        // ---------- Payees, requests, notifications ----------
        foreach (var (_, u) in people)
        {
            db.Payees.AddRange(
                new Payee { OwnerId = u.Id, Name = "City Power & Light", AccountReference = $"CPL-{random.Next(10000, 99999)}", Category = TransactionCategory.Bills },
                new Payee { OwnerId = u.Id, Name = "FiberNet Internet", AccountReference = $"FN-{random.Next(100000, 999999)}", Category = TransactionCategory.Bills },
                new Payee { OwnerId = u.Id, Name = "Maple Court Apartments", AccountReference = $"UNIT-{random.Next(100, 450)}", Category = TransactionCategory.Bills },
                new Payee { OwnerId = u.Id, Name = "Skyline Wireless", AccountReference = $"SKY-{random.Next(1000000, 9999999)}", Category = TransactionCategory.Bills });
        }

        var byHandle = people.ToDictionary(x => x.P.Handle, x => x.U);
        var demo = byHandle["jordan"];
        db.MoneyRequests.AddRange(
            new MoneyRequest { RequesterId = byHandle["alice"].Id, PayerId = demo.Id, Amount = 24.50m, Note = "Lunch at Noodle House", CreatedUtc = now.AddHours(-5) },
            new MoneyRequest { RequesterId = byHandle["carol"].Id, PayerId = demo.Id, Amount = 60m, Note = "Concert tickets", CreatedUtc = now.AddDays(-1) },
            new MoneyRequest { RequesterId = demo.Id, PayerId = byHandle["bob"].Id, Amount = 35m, Note = "Gas for the road trip", CreatedUtc = now.AddDays(-2) },
            new MoneyRequest { RequesterId = demo.Id, PayerId = byHandle["dave"].Id, Amount = 18m, Note = "Movie tickets", Status = MoneyRequestStatus.Declined, CreatedUtc = now.AddDays(-9), RespondedUtc = now.AddDays(-8) },
            new MoneyRequest { RequesterId = byHandle["bob"].Id, PayerId = demo.Id, Amount = 22m, Note = "Pizza night", Status = MoneyRequestStatus.Paid, CreatedUtc = now.AddDays(-14), RespondedUtc = now.AddDays(-14) });

        foreach (var (p, u) in people)
            db.Notifications.Add(new Notification { UserId = u.Id, Title = "Welcome to SimpleBank", Body = "Your accounts are ready. Explore your dashboard to see where your money goes.", Icon = "sparkles", Link = "/Dashboard", CreatedUtc = start, IsRead = true });
        db.Notifications.AddRange(
            new Notification { UserId = demo.Id, Title = "Alice Chen requested $24.50", Body = "“Lunch at Noodle House”", Icon = "request", Link = "/Requests", CreatedUtc = now.AddHours(-5) },
            new Notification { UserId = demo.Id, Title = "Carol Okafor requested $60.00", Body = "“Concert tickets”", Icon = "request", Link = "/Requests", CreatedUtc = now.AddDays(-1) },
            new Notification { UserId = demo.Id, Title = "Paycheck deposited", Body = "Your payroll deposit from Northwind Labs has arrived.", Icon = "arrow-down", Link = "/Transactions?category=Income", CreatedUtc = now.AddDays(-3), IsRead = true },
            new Notification { UserId = demo.Id, Title = "Dave Patel declined your request", Body = "Your request for $18.00 was declined.", Icon = "x", Link = "/Requests?tab=outgoing", CreatedUtc = now.AddDays(-8), IsRead = true },
            new Notification { UserId = byHandle["bob"].Id, Title = "Jordan Rivera requested $35.00", Body = "“Gas for the road trip”", Icon = "request", Link = "/Requests", CreatedUtc = now.AddDays(-2) });

        await db.SaveChangesAsync();
        _ = admin;

        static decimal Round(decimal d) => decimal.Round(d, 2);
        static string Ref(string prefix) => LedgerPoster.NewReference(prefix);
        BankAccount NewAccount(ApplicationUser owner, string name, AccountType type, bool primary, DateTime opened) =>
            new() { OwnerId = owner.Id, Owner = owner, Name = name, Type = type, IsPrimary = primary, OpenedUtc = opened };
    }

    private static void TryPost(LedgerPoster poster, BankAccount account, decimal amount, TransactionType type, TransactionCategory cat, string description, string? counterparty, DateTime when)
    {
        if (amount < 0 && account.Balance + amount < 0) return; // skip spending the persona can't afford
        poster.Post(account, amount, type, cat, description, LedgerPoster.NewReference(type == TransactionType.BillPayment ? "BIL" : "POS"), counterparty, when);
    }

    private static void TryTransfer(LedgerPoster poster, BankAccount from, BankAccount to, decimal amount, string memo, DateTime when)
    {
        if (from.Balance - amount < 400m) return; // keep a cushion in checking
        var reference = LedgerPoster.NewReference("TRF");
        poster.Post(from, -amount, TransactionType.TransferOut, TransactionCategory.Transfer, $"{memo} to {to.Name}", reference, to.Name, when);
        poster.Post(to, amount, TransactionType.TransferIn, TransactionCategory.Transfer, $"{memo} from {from.Name}", reference, from.Name, when);
    }

    private static async Task<ApplicationUser> CreateUserAsync(UserManager<ApplicationUser> users, string email, string handle, string name, string color, DateTime created, string role)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, EmailConfirmed = true, FullName = name,
            Handle = handle, AvatarColor = color, CreatedUtc = created
        };
        var result = await users.CreateAsync(user, DemoPassword);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);
        return user;
    }
}
