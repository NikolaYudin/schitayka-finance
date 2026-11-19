using System.Net;

namespace SchitayKa.Tests.Integration;

/// <summary>Полный HTTP-конвейер: первичная настройка, вход, авторизация и смена пароля.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class SetupAndAuthTests
{
    [Fact]
    public async Task Setup_ThenLogin_MeReturnsMaster_AndHealthWorks()
    {
        await using var app = await IntegrationApp.StartAsync();
        var client = app.Client;

        // На чистой базе требуется первичная настройка.
        using var statusResponse = await client.GetWithTokenAsync("/api/setup/status");
        using var status = await statusResponse.ReadJsonAsync();
        Assert.True(status.RootElement.GetProperty("required").GetBoolean());

        using var setupResponse = await client.PostJsonAsync("/api/setup/admin",
            new { email = "admin@family.ru", name = "Администратор", password = "StrongPass123!" });
        await setupResponse.ReadTokenAsync();

        // Вход по созданным учётным данным.
        using var loginResponse = await client.PostJsonAsync("/api/auth/login",
            new { email = "admin@family.ru", password = "StrongPass123!" });
        var token = await loginResponse.ReadTokenAsync();

        using var meResponse = await client.GetWithTokenAsync("/api/auth/me", token);
        using var me = await meResponse.ReadJsonAsync();
        Assert.Equal("Master", me.RootElement.GetProperty("role").GetString());
        Assert.True(me.RootElement.GetProperty("isMaster").GetBoolean());
        Assert.True(me.RootElement.GetProperty("canManageUsers").GetBoolean());

        // Роль Master пропускает запрос к эндпоинту управления пользователями.
        using var usersResponse = await client.GetWithTokenAsync("/api/admin/users", token);
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);

        // После создания администратора мастер больше не нужен.
        using var statusAfter = await client.GetWithTokenAsync("/api/setup/status");
        using var statusAfterJson = await statusAfter.ReadJsonAsync();
        Assert.False(statusAfterJson.RootElement.GetProperty("required").GetBoolean());

        using var healthResponse = await client.GetWithTokenAsync("/api/health");
        using var health = await healthResponse.ReadJsonAsync();
        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ChangePassword_InvalidatesOldToken_AndNewLoginWorks()
    {
        await using var app = await IntegrationApp.StartAsync();
        var client = app.Client;
        var token = await client.CreateAdminAsync();

        using var changeResponse = await client.PostJsonAsync("/api/auth/change-password",
            new { currentPassword = "StrongPass123!", newPassword = "EvenStronger456!" }, token);
        using var change = await changeResponse.ReadJsonAsync();
        Assert.True(change.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(1, change.RootElement.GetProperty("passwordVersion").GetInt32());

        // Старый JWT привязан к старой версии пароля и отклоняется middleware.
        using var oldTokenResponse = await client.GetWithTokenAsync("/api/auth/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenResponse.StatusCode);

        // Старый пароль больше не работает, новый — работает.
        using var oldLogin = await client.PostJsonAsync("/api/auth/login",
            new { email = "admin@family.ru", password = "StrongPass123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var newLogin = await client.PostJsonAsync("/api/auth/login",
            new { email = "admin@family.ru", password = "EvenStronger456!" });
        var newToken = await newLogin.ReadTokenAsync();

        using var meResponse = await client.GetWithTokenAsync("/api/auth/me", newToken);
        using var me = await meResponse.ReadJsonAsync();
        Assert.Equal("admin@family.ru", me.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task AdminUsers_RequiresAuthentication_AndMasterRole()
    {
        await using var app = await IntegrationApp.StartAsync();
        var client = app.Client;

        // Без токена — 401.
        using var anonymousResponse = await client.GetWithTokenAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var masterToken = await client.CreateAdminAsync();
        using var createResponse = await client.PostJsonAsync("/api/admin/users",
            new { email = "member@family.ru", name = "Участник", password = "MemberPass123!" }, masterToken);
        using var created = await createResponse.ReadJsonAsync();
        Assert.Equal("Member", created.RootElement.GetProperty("role").GetString());

        using var memberLogin = await client.PostJsonAsync("/api/auth/login",
            new { email = "member@family.ru", password = "MemberPass123!" });
        var memberToken = await memberLogin.ReadTokenAsync();

        // Токен участника не даёт доступа к административному эндпоинту — 403.
        using var memberResponse = await client.GetWithTokenAsync("/api/admin/users", memberToken);
        Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
    }
}
