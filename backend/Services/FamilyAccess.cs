using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Доступ к семейным счетам: свои счета + счета членов своей семьи.
/// Используется в AccountsController, TransactionsController и BudgetController,
/// чтобы правило «кому видны счета» было в одном месте.
/// </summary>
public static class FamilyAccess
{
    /// <summary>Коды пользователей, чьими счетами может управлять текущий: он сам + семья.</summary>
    public static async Task<List<int>> ManageableUserIdsAsync(AppDbContext db, int uid)
    {
        var me = await db.Users.FindAsync(uid);
        if (me is null) return [];
        if (string.IsNullOrEmpty(me.FamilyCode)) return [uid];

        return await db.Users
            .Where(u => u.FamilyCode == me.FamilyCode)
            .Select(u => u.Id)
            .ToListAsync();
    }

    /// <summary>Можно ли текущему пользователю работать с этим счётом.</summary>
    public static async Task<bool> CanManageAccountAsync(AppDbContext db, int uid, int accountId)
    {
        var ids = await ManageableUserIdsAsync(db, uid);
        return await db.Accounts.AnyAsync(a => a.Id == accountId && ids.Contains(a.UserId));
    }

    /// <summary>Является ли счёт семейным (принадлежит не мне, а другому члену семьи).</summary>
    public static async Task<bool> IsFamilyAccountAsync(AppDbContext db, int uid, int accountId)
    {
        var acc = await db.Accounts.FindAsync(accountId);
        if (acc is null || acc.UserId == uid) return false;
        var ids = await ManageableUserIdsAsync(db, uid);
        return ids.Contains(acc.UserId);
    }
}
