using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Procurement.Api;
using Procurement.Application;

namespace Procurement.Tests;

public sealed class IntegrationTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private async Task<CreatedOrder> Create(string? key = null)
    {
        using var response = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(), key: key ?? Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<CreatedOrder>())!;
    }

    [Fact]
    public async Task Draft_is_visible_after_api_restart_and_seed_is_repeatable()
    {
        var created = await Create();
        using var restarted = new ApiFactory(f.Connection, f.Key, new());
        using var client = restarted.CreateClient();
        using var response = await f.Send(HttpMethod.Get, $"/api/purchase-orders/{created.OrderId}", client: client);
        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<OrderDetail>())!;
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal(150, detail.Lines.Sum(x => x.Quantity));
        Assert.Equal("buyer", detail.CreatedBy);
        Assert.Equal("Draft", detail.Status);
        Assert.Equal(DemoSeed.FactoryA, detail.FactoryId);
        await f.SeedAgain();
        Assert.Equal(2, await f.Db(db => db.Factories.CountAsync()));
        Assert.Equal(3, await f.Db(db => db.Skus.CountAsync()));
        Assert.Equal(4, await f.Db(db => db.Users.CountAsync()));
        using var list = await f.Send(HttpMethod.Get, "/api/purchase-orders?pageSize=100");
        Assert.Contains((await list.Content.ReadFromJsonAsync<OrderPage>())!.Items, x => x.Id == created.OrderId);
    }

    [Fact]
    public async Task Same_key_replays_original_response_and_conflicting_content_returns_409()
    {
        var key = Guid.NewGuid().ToString();
        var original = await Create(key);
        var reversed = ApiFixture.Draft() with { Lines = ApiFixture.Draft().Lines!.Reverse().ToArray() };
        using var repeat = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: reversed, key: key);
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal(original, await repeat.Content.ReadFromJsonAsync<CreatedOrder>());
        using var conflict = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(101), key: key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, await f.Db(db => db.Audit.CountAsync(x => x.OrderId == original.OrderId)));
    }

    [Fact]
    public async Task Concurrent_identical_requests_create_one_order_and_one_audit()
    {
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(), key: key)));
        var results = new List<CreatedOrder>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                results.Add((await response.Content.ReadFromJsonAsync<CreatedOrder>())!);
            }
        }
        Assert.Single(results.Distinct());
        Assert.Equal(1, await f.Db(db => db.Audit.CountAsync(x => x.OrderId == results[0].OrderId)));
        Assert.Equal(1, await f.Db(db => db.Requests.CountAsync(x => x.Key == key)));
    }

    [Fact]
    public async Task Concurrent_different_content_with_one_key_has_only_one_winner()
    {
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(
            f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(100), key: key),
            f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(101), key: key));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();
        Assert.Equal(1, await f.Db(db => db.Requests.CountAsync(x => x.Key == key)));
    }

    [Fact]
    public async Task Failure_after_sql_save_rolls_back_order_audit_and_idempotency_then_retry_succeeds()
    {
        var key = Guid.NewGuid().ToString();
        var before = await f.Db(db => db.Orders.CountAsync());
        var auditBefore = await f.Db(db => db.Audit.CountAsync());
        f.Failure.Armed = 1;
        using var failed = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(), key: key);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, await f.Db(db => db.Orders.CountAsync()));
        Assert.Equal(auditBefore, await f.Db(db => db.Audit.CountAsync()));
        Assert.Equal(0, await f.Db(db => db.Requests.CountAsync(x => x.Key == key)));
        await Create(key);
    }

    [Fact]
    public async Task Invalid_business_inputs_do_not_persist_orders()
    {
        var valid = ApiFixture.Draft();
        var before = await f.Db(db => db.Orders.CountAsync());
        foreach (var request in new[]
        {
            valid with { Lines = [] }, valid with { Lines = null }, valid with { DeliveryDate = default },
            ApiFixture.Draft(0), ApiFixture.Draft(-1), ApiFixture.Draft(factory: Guid.NewGuid()),
            ApiFixture.Draft(sku: Guid.NewGuid()), valid with { Lines = [new(DemoSeed.SkuM, 100), new(DemoSeed.SkuM, 50)] }
        })
        {
            using var response = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: request, key: Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(before, await f.Db(db => db.Orders.CountAsync()));
    }

    [Theory]
    [InlineData("1.5")] [InlineData("2147483648")] [InlineData("\"one\"")]
    public async Task Non_integer_or_overflow_quantity_is_rejected_at_http_boundary(string quantity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/purchase-orders");
        request.Headers.Authorization = new("Bearer", f.Tokens["buyer"]);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = new StringContent($$"""
            {"factoryId":"{{DemoSeed.FactoryA}}","deliveryDate":"2026-10-31","lines":[{"skuId":"{{DemoSeed.SkuM}}","quantity":{{quantity}}}]}
            """, System.Text.Encoding.UTF8, "application/json");
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Database_enforces_positive_quantity_unique_sku_and_local_foreign_keys()
    {
        var created = await Create();
        var quantity = await Assert.ThrowsAsync<PostgresException>(() => f.Db(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_lines SET \"Quantity\" = 0 WHERE \"OrderId\" = {created.OrderId}")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, quantity.SqlState);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => f.Db(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_lines SET \"SkuId\" = {DemoSeed.SkuM} WHERE \"OrderId\" = {created.OrderId} AND \"SkuId\" = {DemoSeed.SkuL}")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var unknown = await Assert.ThrowsAsync<PostgresException>(() => f.Db(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE purchase_orders SET \"FactoryId\" = {Guid.NewGuid()} WHERE \"Id\" = {created.OrderId}")));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknown.SqlState);
    }

    [Fact]
    public async Task Both_factories_and_quality_cannot_read_or_create_brand_drafts_even_with_known_ids()
    {
        var order = await Create();
        foreach (var user in new[] { "factory-a", "factory-b", "quality" })
        {
            using var detail = await f.Send(HttpMethod.Get, $"/api/purchase-orders/{order.OrderId}", user);
            using var list = await f.Send(HttpMethod.Get, "/api/purchase-orders", user);
            using var create = await f.Send(HttpMethod.Post, "/api/purchase-orders", user, ApiFixture.Draft(factory: DemoSeed.FactoryB), Guid.NewGuid().ToString());
            using var factories = await f.Send(HttpMethod.Get, "/api/factories", user);
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, factories.StatusCode);
        }
    }

    [Fact]
    public async Task Missing_invalid_expired_wrong_issuer_and_wrong_audience_tokens_are_rejected()
    {
        using var anonymous = await f.Send(HttpMethod.Get, "/api/purchase-orders", user: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var kind in new[] { "valid", "invalid", "expired", "issuer", "audience", "signature", "subject", "factory" })
        {
            var claims = new List<Claim> { new("role", kind == "factory" ? "Factory" : "Buyer") };
            if (kind != "subject") claims.Add(new("sub", "buyer"));
            var signingKey = kind == "signature" ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)
                : Convert.FromBase64String(f.Key);
            var jwt = new JwtSecurityToken(kind == "issuer" ? "other" : "scm-procurement",
                kind == "audience" ? "other" : "scm-local", claims,
                DateTime.UtcNow.AddHours(-2), kind == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
                new SigningCredentials(new SymmetricSecurityKey(signingKey), SecurityAlgorithms.HmacSha256));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/purchase-orders");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", kind == "invalid" ? "invalid" : new JwtSecurityTokenHandler().WriteToken(jwt));
            using var response = await f.Client.SendAsync(request);
            Assert.Equal(kind == "valid" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Login_rejects_bad_password_and_missing_idempotency_key_is_400()
    {
        using var login = await f.Client.PostAsJsonAsync("/api/demo-auth/login", new LoginRequest("buyer", "incorrect-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        using var create = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft());
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Health_openapi_and_pagination_are_available()
    {
        foreach (var path in new[] { "/health/live", "/health/ready", "/openapi/v1.json" })
        {
            using var response = await f.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var invalid = await f.Send(HttpMethod.Get, "/api/purchase-orders?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Demo_login_and_openapi_are_not_exposed_in_production()
    {
        using var production = new ApiFactory(f.Connection, f.Key, new(), "Production");
        using var client = production.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/demo-auth/login", new LoginRequest("buyer", "not-a-real-password"));
        using var openApi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, openApi.StatusCode);
    }
}
