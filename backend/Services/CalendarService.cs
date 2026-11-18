using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>Источник события календаря (Задача 5).</summary>
public enum CalendarSource
{
    UserEvent = 0,        // пользовательское событие/напоминание
    Obligation = 1,       // следующий обязательный платёж
    DebtDue = 2,          // срок возврата по долгу
    DepositMaturity = 3,  // окончание вклада
    InterestAccrual = 4,  // начисление/выплата процентов
}

/// <summary>Единая строка календаря независимо от источника.</summary>
public sealed record CalendarItem(
    string Key,
    int Id,
    int Source,
    string SourceName,
    string Title,
    DateOnly Date,
    string? Time,
    decimal? Amount,
    bool IsOverdue,
    int? DebtId,
    int? ObligationId,
    int? AccountId,
    bool Remind,
    string Note,
    string? TypeName,
    int OwnerId,
    string? OwnerName);

/// <summary>
/// Календарь событий (Задача 5): объединяет даты из долгов, платежей, вкладов,
/// начислений процентов и пользовательских событий. Суммы никуда не агрегируются —
/// календарь только показывает даты, не влияя на бюджет и аналитику.
/// </summary>
public sealed class CalendarService
{
    private readonly AppDbContext _db;
    public CalendarService(AppDbContext db) => _db = db;

    public sealed record CalendarEventInput(
        string? Title, DateOnly? Date, string? Time, int Type, decimal? Amount,
        bool Remind, string? Note, int? DebtId, int? ObligationId, int? AccountId);

    /// <summary>События в диапазоне дат; sources — номера источников из CalendarSource.</summary>
    public async Task<List<CalendarItem>> GetAsync(
        int userId, DateOnly from, DateOnly to, IReadOnlyCollection<int>? sources = null,
        IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var today = DateOnly.FromDateTime(DateTime.Today);
        var owners = await _db.Users.Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Name }).ToDictionaryAsync(u => u.Id, u => u.Name);
        string? OwnerName(int id) => owners.TryGetValue(id, out var name) ? name : null;
        var wanted = sources is { Count: > 0 } ? sources.ToHashSet() : null;
        var include = (CalendarSource s) => wanted is null || wanted.Contains((int)s);
        var items = new List<CalendarItem>();

        if (from > to) (from, to) = (to, from);

        if (include(CalendarSource.UserEvent))
        {
            var events = await _db.CalendarEvents
                .Include(e => e.Debt)
                .Include(e => e.Obligation)
                .Include(e => e.Account)
                .Where(e => ids.Contains(e.UserId) && e.Date >= from && e.Date <= to)
                .ToListAsync();
            items.AddRange(events.Select(e => new CalendarItem(
                $"event:{e.Id}", e.Id, (int)CalendarSource.UserEvent, "Событие",
                e.Title, e.Date, NormalizeTime(e.Time), e.Amount, e.Date < today,
                e.DebtId, e.ObligationId, e.AccountId, e.Remind, e.Note ?? "",
                TypeName((CalendarEventType)e.Type), e.UserId, OwnerName(e.UserId))));
        }

        if (include(CalendarSource.Obligation))
        {
            var obligations = await _db.Obligations
                .Where(o => ids.Contains(o.UserId) && o.Status == ObligationStatus.Active
                    && o.NextPaymentDate >= from && o.NextPaymentDate <= to)
                .Select(o => new { o.Id, o.UserId, o.Name, o.Payee, o.NextPaymentDate, o.Amount, o.Remind })
                .ToListAsync();
            items.AddRange(obligations.Select(o => new CalendarItem(
                $"obligation:{o.Id}", o.Id, (int)CalendarSource.Obligation, "Платёж",
                o.Name, o.NextPaymentDate, null, o.Amount, o.NextPaymentDate < today,
                null, o.Id, null, o.Remind, o.Payee, null, o.UserId, OwnerName(o.UserId))));
        }

        if (include(CalendarSource.DebtDue))
        {
            var debts = await _db.Debts
                .Where(d => ids.Contains(d.UserId) && d.Status == DebtStatus.Active
                    && d.RemainingAmount > 0 && d.DueDate >= from && d.DueDate <= to)
                .Select(d => new { d.Id, d.UserId, d.Name, d.DueDate, d.RemainingAmount })
                .ToListAsync();
            items.AddRange(debts.Select(d => new CalendarItem(
                $"debt:{d.Id}", d.Id, (int)CalendarSource.DebtDue, "Срок долга",
                $"Срок возврата: {d.Name}", d.DueDate!.Value, null, d.RemainingAmount,
                d.DueDate < today, d.Id, null, null, false, "", null, d.UserId, OwnerName(d.UserId))));
        }

        if (include(CalendarSource.DepositMaturity))
        {
            // MaturesAt хранится как DateTime — сравниваем по календарным датам в C#,
            // чтобы не зависить от часового пояса SQLite.
            var accounts = await _db.Accounts
                .Where(a => ids.Contains(a.UserId) && !a.IsArchived && a.MaturesAt != null)
                .Select(a => new { a.Id, a.UserId, a.Name, a.MaturesAt, a.Balance })
                .ToListAsync();
            var maturing = accounts
                .Where(a => a.MaturesAt is DateTime dt
                    && DateOnly.FromDateTime(dt) >= from && DateOnly.FromDateTime(dt) <= to)
                .Select(a => new CalendarItem(
                    $"deposit:{a.Id}", a.Id, (int)CalendarSource.DepositMaturity, "Вклад",
                    $"Окончание вклада: {a.Name}", DateOnly.FromDateTime(a.MaturesAt!.Value),
                    null, a.Balance, DateOnly.FromDateTime(a.MaturesAt.Value) < today,
                    null, null, a.Id, false, "", null, a.UserId, OwnerName(a.UserId)));
            items.AddRange(maturing);
        }

        if (include(CalendarSource.InterestAccrual))
        {
            var accruals = await _db.InterestAccruals
                .Include(a => a.Account)
                .Where(a => ids.Contains(a.UserId) && !a.IsPaid)
                .ToListAsync();
            var inRange = accruals
                .Where(a => DateOnly.FromDateTime(a.To) >= from && DateOnly.FromDateTime(a.To) <= to)
                .Select(a => new CalendarItem(
                    $"interest:{a.Id}", a.Id, (int)CalendarSource.InterestAccrual, "Проценты",
                    $"Начисление процентов: {a.Account.Name}", DateOnly.FromDateTime(a.To),
                    null, a.Net, DateOnly.FromDateTime(a.To) < today,
                    null, null, a.AccountId, false, "", null, a.UserId, OwnerName(a.UserId)));
            items.AddRange(inRange);
        }

        return items
            .OrderBy(i => i.Date)
            .ThenBy(i => i.Time ?? "99:99")
            .ThenBy(i => i.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Ближайшие события: от сегодня на заданное число дней.</summary>
    public async Task<List<CalendarItem>> UpcomingAsync(
        int userId, int take = 5, int days = 30, IReadOnlyCollection<int>? sources = null,
        IReadOnlyCollection<int>? scopeIds = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Просроченные события тоже показываем — иначе они «пропадают» из ближайших.
        var all = await GetAsync(userId, today.AddDays(-30), today.AddDays(Math.Max(1, days)),
            sources, scopeIds);
        return all.Take(Math.Clamp(take, 1, 50)).ToList();
    }

    /// <summary>Одно пользовательское событие (для редактирования).</summary>
    public Task<CalendarEvent?> GetEventAsync(
        int userId, int id, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        return _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && ids.Contains(e.UserId));
    }

    /// <summary>Создаёт пользовательское событие.</summary>
    public async Task<(bool Ok, string? Error, CalendarEvent? Event)> CreateAsync(
        int userId, CalendarEventInput input, IReadOnlyCollection<int>? scopeIds = null)
    {
        var violation = await ValidateAsync(userId, input, scopeIds);
        if (violation is not null) return (false, violation, null);

        var entity = new CalendarEvent
        {
            UserId = userId,
            Title = input.Title!.Trim(),
            Date = input.Date!.Value,
            Time = NormalizeTime(input.Time),
            Type = (CalendarEventType)input.Type,
            Amount = input.Amount,
            Remind = input.Remind,
            Note = input.Note?.Trim() ?? "",
            DebtId = input.DebtId,
            ObligationId = input.ObligationId,
            AccountId = input.AccountId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.CalendarEvents.Add(entity);
        await _db.SaveChangesAsync();
        return (true, null, entity);
    }

    /// <summary>Изменяет пользовательское событие.</summary>
    public async Task<(bool Ok, string? Error)> UpdateAsync(
        int userId, int id, CalendarEventInput input, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var entity = await _db.CalendarEvents
            .FirstOrDefaultAsync(e => e.Id == id && ids.Contains(e.UserId));
        if (entity is null) return (false, "Событие не найдено");

        var violation = await ValidateAsync(userId, input, ids);
        if (violation is not null) return (false, violation);

        entity.Title = input.Title!.Trim();
        entity.Date = input.Date!.Value;
        entity.Time = NormalizeTime(input.Time);
        entity.Type = (CalendarEventType)input.Type;
        entity.Amount = input.Amount;
        entity.Remind = input.Remind;
        entity.Note = input.Note?.Trim() ?? "";
        entity.DebtId = input.DebtId;
        entity.ObligationId = input.ObligationId;
        entity.AccountId = input.AccountId;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Удаляет пользовательское событие.</summary>
    public async Task<(bool Ok, string? Error)> DeleteAsync(
        int userId, int id, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var entity = await _db.CalendarEvents
            .FirstOrDefaultAsync(e => e.Id == id && ids.Contains(e.UserId));
        if (entity is null) return (false, "Событие не найдено");
        _db.CalendarEvents.Remove(entity);
        await _db.SaveChangesAsync();
        return (true, null);
    }

    // ---------- Вспомогательные методы ----------

    private static string? NormalizeTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        return TimeOnly.TryParse(time.Trim(), out var parsed)
            ? parsed.ToString("HH:mm")
            : null;
    }

    private static string TypeName(CalendarEventType type) => type switch
    {
        CalendarEventType.Reminder => "Напоминание",
        CalendarEventType.Meeting => "Встреча",
        CalendarEventType.Other => "Прочее",
        _ => "Событие",
    };

    private async Task<string?> ValidateAsync(
        int userId, CalendarEventInput input, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        if (string.IsNullOrWhiteSpace(input.Title))
            return "Название события обязательно";
        if (input.Date is null)
            return "Укажите дату события";
        if (!string.IsNullOrWhiteSpace(input.Time) && !TimeOnly.TryParse(input.Time.Trim(), out _))
            return "Время указано неверно";
        if (!Enum.IsDefined((CalendarEventType)input.Type))
            return "Неизвестный тип события";
        if (input.Amount is decimal amount && (amount < 0 || decimal.Round(amount, 2) != amount))
            return "Неверная сумма: укажите неотрицательное значение с точностью до копеек";
        if (input.DebtId is int debtId
            && await _db.Debts.CountAsync(d => d.Id == debtId && ids.Contains(d.UserId)) == 0)
            return "Долг не найден";
        if (input.ObligationId is int obligationId
            && await _db.Obligations.CountAsync(o => o.Id == obligationId && ids.Contains(o.UserId)) == 0)
            return "Платёж не найден";
        if (input.AccountId is int accountId
            && await _db.Accounts.CountAsync(a => a.Id == accountId && ids.Contains(a.UserId)) == 0)
            return "Счёт не найден";
        return null;
    }
}
