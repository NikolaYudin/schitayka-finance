using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>
/// Задача 0.3/N3: переводы между счетами семьи. Решение пользователя (17.11.2026):
/// перевод делается от владельца счёта-отправителя (запись Transfer и расходная половина
/// принадлежат ему, доходная — владельцу счёта-получателя), при этом перевод виден
/// всем членам семьи и может быть отменён любым из них.
/// </summary>
public class Task03N3FamilyTransferTests
{
    private static async Task<(SqliteConnection Conn, AppDbContext Db, User Me, User Spouse)> CreateFamilyAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var me = new User { Email = "me@schitayka.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM" };
        var spouse = new User { Email = "sp@schitayka.ru", Name = "Супруг(а)", PasswordHash = "x:y", FamilyCode = "FAM" };
        db.Users.AddRange(me, spouse);
        await db.SaveChangesAsync();

        return (conn, db, me, spouse);
    }

    private static Account Acc(int userId, string name, decimal start) => new()
    {
        UserId = userId, Name = name, Type = AccountType.DebitCard,
        StartingBalance = start, Balance = start,
    };

    [Fact]
    public async Task Create_BetweenFamilyAccounts_TransferBelongsToFromOwner_HalvesToAccountOwners()
    {
        var (conn, db, me, spouse) = await CreateFamilyAsync();
        using var _ = conn; using var __ = db;

        var myAcc = Acc(me.Id, "Моя карта", 10000m);
        var spouseAcc = Acc(spouse.Id, "Карта супруга", 0m);
        db.Accounts.AddRange(myAcc, spouseAcc);
        await db.SaveChangesAsync();

        var service = new TransferService(db);
        var result = await service.CreateAsync(me.Id, myAcc.Id, spouseAcc.Id, 2500m, new DateOnly(2026, 11, 14), null);
        Assert.True(result.Ok);

        var transfer = result.Transfer!;
        Assert.Equal(me.Id, transfer.UserId); // перевод — от владельца счёта-отправителя

        var outTx = await db.Transactions.SingleAsync(t => t.Id == transfer.FromTxId);
        var inTx = await db.Transactions.SingleAsync(t => t.Id == transfer.ToTxId);
        Assert.Equal(me.Id, outTx.UserId);       // расходная половина — отправителю
        Assert.Equal(spouse.Id, inTx.UserId);    // доходная половина — получателю

        // Балансы обоих владельцев пересчитаны
        Assert.Equal(7500m, (await db.Accounts.FindAsync(myAcc.Id))!.Balance);
        Assert.Equal(2500m, (await db.Accounts.FindAsync(spouseAcc.Id))!.Balance);
    }

    [Fact]
    public async Task List_TransferVisibleToAllFamilyMembers()
    {
        var (conn, db, me, spouse) = await CreateFamilyAsync();
        using var _ = conn; using var __ = db;

        var myAcc = Acc(me.Id, "Моя карта", 10000m);
        var spouseAcc = Acc(spouse.Id, "Карта супруга", 0m);
        db.Accounts.AddRange(myAcc, spouseAcc);
        await db.SaveChangesAsync();

        var service = new TransferService(db);
        var created = await service.CreateAsync(me.Id, myAcc.Id, spouseAcc.Id, 2500m, new DateOnly(2026, 11, 14), null);
        Assert.True(created.Ok);
        Assert.NotNull(created.Transfer);

        // Супруг видит перевод, хотя запись принадлежит мне
        var forSpouse = await service.ListAsync(spouse.Id, month: 11, year: 2026);
        Assert.Single(forSpouse);
        Assert.Equal(created.Transfer!.Id, forSpouse[0].Id);

        // И детали тоже видны
        var details = await service.GetAsync(spouse.Id, created.Transfer!.Id);
        Assert.NotNull(details);
    }

    [Fact]
    public async Task Cancel_ByFamilyMember_RemovesHalvesOfBothOwnersAndRestoresBalances()
    {
        var (conn, db, me, spouse) = await CreateFamilyAsync();
        using var _ = conn; using var __ = db;

        var myAcc = Acc(me.Id, "Моя карта", 10000m);
        var spouseAcc = Acc(spouse.Id, "Карта супруга", 0m);
        db.Accounts.AddRange(myAcc, spouseAcc);
        await db.SaveChangesAsync();

        var service = new TransferService(db);
        var created = await service.CreateAsync(me.Id, myAcc.Id, spouseAcc.Id, 2500m, new DateOnly(2026, 11, 14), null);
        Assert.True(created.Ok);

        // Супруг отменяет мой перевод
        var (ok, error) = await service.CancelAsync(spouse.Id, created.Transfer!.Id);
        Assert.True(ok, error);

        Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Empty(await db.Transfers.ToListAsync());
        Assert.Equal(10000m, (await db.Accounts.FindAsync(myAcc.Id))!.Balance);
        Assert.Equal(0m, (await db.Accounts.FindAsync(spouseAcc.Id))!.Balance);
    }

    [Fact]
    public async Task ForeignUser_CannotSeeOrCancelFamilyTransfer()
    {
        var (conn, db, me, spouse) = await CreateFamilyAsync();
        using var _ = conn; using var __ = db;

        var outsider = new User { Email = "out@schitayka.ru", Name = "Чужой", PasswordHash = "x:y" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();

        var myAcc = Acc(me.Id, "Моя карта", 10000m);
        var spouseAcc = Acc(spouse.Id, "Карта супруга", 0m);
        db.Accounts.AddRange(myAcc, spouseAcc);
        await db.SaveChangesAsync();

        var service = new TransferService(db);
        var created = await service.CreateAsync(me.Id, myAcc.Id, spouseAcc.Id, 2500m, new DateOnly(2026, 11, 14), null);
        Assert.True(created.Ok);

        Assert.Empty(await service.ListAsync(outsider.Id));
        Assert.Null(await service.GetAsync(outsider.Id, created.Transfer!.Id));
        var (ok, _) = await service.CancelAsync(outsider.Id, created.Transfer.Id);
        Assert.False(ok);
        // Перевод цел
        Assert.Single(await db.Transfers.ToListAsync());
    }

    [Fact]
    public async Task TransferTransactionIds_AreHiddenFromJournalOfBothFamilyMembers()
    {
        var (conn, db, me, spouse) = await CreateFamilyAsync();
        using var _ = conn; using var __ = db;

        var myAcc = Acc(me.Id, "Моя карта", 10000m);
        var spouseAcc = Acc(spouse.Id, "Карта супруга", 0m);
        db.Accounts.AddRange(myAcc, spouseAcc);
        await db.SaveChangesAsync();

        var service = new TransferService(db);
        var created = await service.CreateAsync(me.Id, myAcc.Id, spouseAcc.Id, 2500m, new DateOnly(2026, 11, 14), null);
        Assert.True(created.Ok);

        var myHidden = await service.TransferTransactionIdsAsync(me.Id);
        var spouseHidden = await service.TransferTransactionIdsAsync(spouse.Id);

        Assert.Subset(myHidden, new HashSet<int> { created.Transfer!.FromTxId!.Value, created.Transfer.ToTxId!.Value });
        Assert.Subset(spouseHidden, new HashSet<int> { created.Transfer.FromTxId!.Value, created.Transfer.ToTxId!.Value });
    }
}
