using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;

namespace SimpleBankMvc.Services;

public static class DbContextExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/>, saves, and commits in one database transaction.
    /// On any failure the transaction rolls back and the change tracker is cleared, so balances
    /// mutated in memory never leak into later queries in the same request.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(this ApplicationDbContext db, Func<Task<T>> work)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var result = await work();
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return result;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new BankingException("We couldn't complete that request. Please try again.");
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
