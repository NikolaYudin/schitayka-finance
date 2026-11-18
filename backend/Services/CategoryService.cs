using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Общие категории семьи. Категория по-прежнему имеет UserId (создатель), но если
/// у пользователя есть семья, все её члены видят и редактируют один и тот же набор.
///
/// При вступлении в семью у разных её членов могли существовать одноимённые категории
/// с разными Id. NormalizeFamilyAsync объединяет такие дубли: выбирает каноническую
/// категорию и перенаправляет на неё операции, лимиты, обязательства и правила.
/// </summary>
public static class CategoryService
{
    /// <summary>Категории, доступные пользователю: личные + общие категории его семьи.</summary>
    public static async Task<IQueryable<Category>> AccessibleAsync(AppDbContext db, int uid)
    {
        var family = await FamilyCodeOfAsync(db, uid);
        if (string.IsNullOrEmpty(family))
            return db.Categories.Where(c => c.UserId == uid);

        // Пока нормализация не прошла, категории членов семьи могут не иметь FamilyCode —
        // поэтому включаем и их по UserId.
        var memberIds = await db.Users.Where(u => u.FamilyCode == family)
            .Select(u => u.Id).ToListAsync();
        return db.Categories.Where(c => memberIds.Contains(c.UserId) || c.FamilyCode == family);
    }

    /// <summary>Найти категорию по Id среди доступных пользователю.</summary>
    public static async Task<Category?> FindAsync(AppDbContext db, int uid, int categoryId) =>
        await (await AccessibleAsync(db, uid)).FirstOrDefaultAsync(c => c.Id == categoryId);

    /// <summary>Код семьи пользователя (null — личный режим).</summary>
    public static async Task<string?> FamilyCodeOfAsync(AppDbContext db, int uid) =>
        (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid))?.FamilyCode;

    /// <summary>
    /// Системная категория («Перевод», «Долги», …) в семейном пространстве:
    /// создаётся один раз и видна всем членам семьи.
    /// </summary>
    public static async Task<Category> EnsureSystemAsync(
        AppDbContext db, int uid, string name, TxType type, string color)
    {
        await NormalizeFamilyAsync(db, uid);
        var accessible = await AccessibleAsync(db, uid);
        var existing = await accessible.FirstOrDefaultAsync(c => c.Name == name && c.Type == type);
        if (existing is not null) return existing;

        var family = await FamilyCodeOfAsync(db, uid);
        var category = new Category
        {
            UserId = uid,
            Name = name,
            Type = type,
            Color = color,
            IsSystem = true,
            FamilyCode = family,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    /// <summary>
    /// Приводит категории семьи к одному набору: помечает FamilyCode и сливает
    /// одноимённые категории с разными Id. Идемпотентно.
    /// </summary>
    public static async Task NormalizeFamilyAsync(AppDbContext db, int uid)
    {
        var family = await FamilyCodeOfAsync(db, uid);
        if (string.IsNullOrEmpty(family)) return;

        var memberIds = await db.Users.Where(u => u.FamilyCode == family)
            .Select(u => u.Id).ToListAsync();
        var ownerId = memberIds.Count > 0 ? memberIds.Min() : uid;
        var all = await db.Categories.Where(c => memberIds.Contains(c.UserId)).ToListAsync();

        var duplicates = all
            .GroupBy(c => (Name: c.Name.Trim().ToLowerInvariant(), c.Type))
            .Where(g => g.Count() > 1)
            .ToList();
        var needsFamilyCode = all.Any(c => c.FamilyCode != family);
        if (!needsFamilyCode && duplicates.Count == 0) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            foreach (var category in all)
                category.FamilyCode = family;

            foreach (var group in duplicates)
            {
                var canonical = group.OrderBy(c => c.UserId == ownerId ? 0 : 1).ThenBy(c => c.Id).First();
                var stale = group.Where(c => c.Id != canonical.Id).ToList();
                var staleIds = stale.Select(c => c.Id).ToList();

                canonical.IsSystem |= group.Any(c => c.IsSystem);

                // ExecuteUpdate уходит в БД сразу — так SQLite видит перенос ссылок
                // до удаления дублей, независимо от порядка в change tracker.
                await db.Transactions.Where(t => staleIds.Contains(t.CategoryId))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, canonical.Id));
                await db.Obligations.Where(o =>
                        o.CategoryId != null && staleIds.Contains(o.CategoryId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.CategoryId, canonical.Id));
                await db.AccountRules.Where(r =>
                        (r.MatchCategoryId != null && staleIds.Contains(r.MatchCategoryId.Value))
                        || (r.SetCategoryId != null && staleIds.Contains(r.SetCategoryId.Value)))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.MatchCategoryId,
                            r => r.MatchCategoryId != null && staleIds.Contains(r.MatchCategoryId.Value)
                                ? canonical.Id : r.MatchCategoryId)
                        .SetProperty(r => r.SetCategoryId,
                            r => r.SetCategoryId != null && staleIds.Contains(r.SetCategoryId.Value)
                                ? canonical.Id : r.SetCategoryId));

                // Лимиты личные, но у одного пользователя могло быть по лимиту на каждый
                // дубль — после слияния это нарушит уникальный индекс. Оставляем максимум.
                var limits = await db.BudgetLimits.Where(l => staleIds.Contains(l.CategoryId)).ToListAsync();
                foreach (var l in limits)
                {
                    var conflict = await db.BudgetLimits.FirstOrDefaultAsync(x =>
                        x.Id != l.Id && x.UserId == l.UserId && x.CategoryId == canonical.Id
                        && x.Year == l.Year && x.Month == l.Month);
                    if (conflict is not null)
                    {
                        if (l.Limit > conflict.Limit) conflict.Limit = l.Limit;
                        db.BudgetLimits.Remove(l);
                    }
                    else
                    {
                        l.CategoryId = canonical.Id;
                    }
                }
                await db.SaveChangesAsync();

                await db.Categories.Where(c => staleIds.Contains(c.Id)).ExecuteDeleteAsync();
                foreach (var staleCategory in stale) db.Entry(staleCategory).State = EntityState.Detached;
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>При выходе из семьи её категории больше не общие для этого пользователя.</summary>
    public static async Task DetachOnLeaveAsync(AppDbContext db, int uid, string familyCode)
    {
        var mine = await db.Categories
            .Where(c => c.UserId == uid && c.FamilyCode == familyCode).ToListAsync();
        foreach (var category in mine) category.FamilyCode = null;
        await db.SaveChangesAsync();
    }
}
