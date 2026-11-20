using System.Text.Json.Serialization;

namespace SchitayKa.Mobile.Models;

/// <summary>
/// DTO ответов API СчитайКа. Имена свойств совпадают с JSON бэкенда
/// (сериализация с JsonSerializerDefaults.Web учитывает camelCase).
/// </summary>
public sealed class LoginResponse
{
    public string Token { get; set; } = "";
    public UserInfo User { get; set; } = new();
}

public sealed class UserInfo
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Role { get; set; }
    public bool IsMaster { get; set; }
    public bool CanManageUsers { get; set; }
    public bool HasPassword { get; set; }
    public string? Theme { get; set; }
    public decimal StartingBalance { get; set; }
}

public sealed class SetupStatus
{
    public bool Required { get; set; }
}

public sealed class Summary
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal MonthIncome { get; set; }
    public decimal MonthExpense { get; set; }
    public decimal Balance { get; set; }
    public decimal TotalBalance { get; set; }
    public decimal SavingsRate { get; set; }
    public string ScopeLabel { get; set; } = "";
    public List<AccountRow> Accounts { get; set; } = [];
    public List<Recommendation> Recommendations { get; set; } = [];
}

public sealed class AccountRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public int Type { get; set; }
    public decimal Balance { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal Available { get; set; }
    public string? BankShortName { get; set; }
    public string? BankColor { get; set; }
}

public sealed class Recommendation
{
    public string Level { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class TransactionRow
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public int? AccountId { get; set; }
    public string Date { get; set; } = "";
    public int Type { get; set; }
    public decimal Amount { get; set; }
    public string? Comment { get; set; }
    public string? CategoryName { get; set; }
    public string? AccountName { get; set; }
    [JsonPropertyName("isTransfer")]
    public bool IsTransfer { get; set; }
}

public sealed class CategoryRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Type { get; set; }          // 0 — доход, 1 — расход
    public string Color { get; set; } = "";
    public bool IsSystem { get; set; }
}

/// <summary>Тело запроса создания/изменения операции (TxDto бэкенда).</summary>
public sealed class TransactionRequest
{
    public int CategoryId { get; set; }
    public int Type { get; set; }
    public decimal Amount { get; set; }
    public string Date { get; set; } = "";   // yyyy-MM-dd
    public string? Comment { get; set; }
    public int? AccountId { get; set; }
}

public sealed class TransactionCreated
{
    public int Id { get; set; }
    public int? AppliedRuleId { get; set; }
}

/// <summary>Результат разбора QR-кода чека ФНС (backend ReceiptParseResult).</summary>
public sealed class ReceiptParseResult
{
    public decimal? Amount { get; set; }
    public string Date { get; set; } = "";
    public int Type { get; set; }
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? Merchant { get; set; }
    public string Comment { get; set; } = "";
    public List<string> Warnings { get; set; } = [];
    public List<ReceiptItem>? Items { get; set; }
    public ReceiptQrInfo? Qr { get; set; }
}

public sealed class ReceiptItem
{
    public string Name { get; set; } = "";
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal Amount { get; set; }
}

public sealed class ReceiptQrInfo
{
    public decimal Amount { get; set; }
    public string Fn { get; set; } = "";
    public string FiscalDocument { get; set; } = "";
    public string FiscalSign { get; set; } = "";
}

public sealed class ApiError
{
    public string? Error { get; set; }
    public string? Title { get; set; }
}
