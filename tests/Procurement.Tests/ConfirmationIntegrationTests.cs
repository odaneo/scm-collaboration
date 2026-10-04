using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Procurement.Api;
using Procurement.Application;
using Procurement.Persistence;

namespace Procurement.Tests;

[Collection("PostgreSQL")]
public sealed class ConfirmationIntegrationTests(ApiFixture f)
{
    private static DateOnly Future => ProcurementService.ShanghaiDate(DateTimeOffset.UtcNow).AddDays(14);
    private async Task<CreatedOrder> Create(DateOnly? date = null)
    {
        using var response = await f.Send(HttpMethod.Post, "/api/purchase-orders",
            body: ApiFixture.Draft() with { DeliveryDate = date ?? Future }, key: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedOrder>())!;
    }
    private async Task<OrderOperationResult> Write(Guid id, string suffix, object body, string user = "buyer",
        string? key = null, HttpMethod? method = null)
    {
        using var response = await f.Send(method ?? HttpMethod.Post, $"/api/purchase-orders/{id}/{suffix}", user, body, key ?? Guid.NewGuid().ToString());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderOperationResult>())!;
    }
    private async Task<OrderDetail> Detail(Guid id)
    {
        using var response = await f.Send(HttpMethod.Get, $"/api/purchase-orders/{id}");
        return (await response.Content.ReadFromJsonAsync<OrderDetail>())!;
    }
    private async Task<VersionDetail> Version(Guid id, int version, string user = "buyer")
    {
        var path = user == "buyer" ? $"/api/purchase-orders/{id}/versions/{version}" : $"/api/factory/orders/{id}/versions/{version}";
        using var response = await f.Send(HttpMethod.Get, path, user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VersionDetail>())!;
    }
    private static UpdateDraftRequest Edit(int revision, int quantity, Guid? factory = null) =>
        new(revision, factory ?? DemoSeed.FactoryA, Future, [new(DemoSeed.SkuM, quantity), new(DemoSeed.SkuL, 50)], "调整订购量");

    [Fact]
    public async Task Editing_preserves_ids_replays_original_response_and_records_each_real_change()
    {
        var created = await Create(); var before = await Detail(created.OrderId); var key = Guid.NewGuid().ToString();
        var request = Edit(1, 120);
        var first = await Write(created.OrderId, "draft", request, key: key, method: HttpMethod.Put);
        Assert.Equal(2, first.Revision);
        var after = await Detail(created.OrderId);
        Assert.Equal(before.Lines.Single(x => x.SkuId == DemoSeed.SkuM).Id, after.Lines.Single(x => x.SkuId == DemoSeed.SkuM).Id);
        var noChange = await Write(created.OrderId, "draft", request with { ExpectedRevision = 2 }, method: HttpMethod.Put);
        Assert.Equal(2, noChange.Revision);
        await Write(created.OrderId, "draft", Edit(2, 130), method: HttpMethod.Put);
        var reordered = request with { Lines = request.Lines!.Reverse().ToArray() };
        Assert.Equal(first, await Write(created.OrderId, "draft", reordered, key: key, method: HttpMethod.Put));
        Assert.Equal(3, (await Detail(created.OrderId)).Revision);
        Assert.Equal(2, await f.Db(db => db.Audit.CountAsync(x => x.OrderId == created.OrderId && x.Action == "UpdateDraft")));
        using var conflict = await f.Send(HttpMethod.Put, $"/api/purchase-orders/{created.OrderId}/draft", body: request with { DeliveryDate = Future.AddDays(1) }, key: key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var audit = await f.Send(HttpMethod.Get, $"/api/purchase-orders/{created.OrderId}/audit");
        Assert.Contains((await audit.Content.ReadFromJsonAsync<AuditPage>())!.Items, x => x.Changes is not null && x.Reason == "调整订购量");
    }
    [Fact]
    public async Task Snapshot_survives_line_removal_factory_change_resubmission_and_api_restart()
    {
        var order = await Create();
        var first = await Write(order.OrderId, "submissions", new RevisionRequest(1));
        var v1 = await Version(order.OrderId, 1);
        await Write(order.OrderId, "versions/1/withdraw", new ReasonRequest(first.Revision, "更换工厂和商品"));
        await Write(order.OrderId, "draft", new UpdateDraftRequest(3, DemoSeed.FactoryB, Future.AddDays(1),
            [new(DemoSeed.SkuBlackM, 130)]), method: HttpMethod.Put);
        var second = await Write(order.OrderId, "submissions", new RevisionRequest(4));
        Assert.Equal(2, second.OrderVersion);
        var history = await Version(order.OrderId, 1, "factory-a");
        Assert.Equal("Withdrawn", history.Status); Assert.Null(history.ExpectedRevision);
        Assert.Equal(v1.Lines.ToArray(), history.Lines.ToArray()); Assert.Equal(v1.FactoryId, history.FactoryId);
        using var hidden = await f.Send(HttpMethod.Get, $"/api/factory/orders/{order.OrderId}/versions/2", "factory-a");
        using var forbidden = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/versions/2/accept", "factory-a",
            new { expectedRevision = 5, factoryId = DemoSeed.FactoryB }, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(DemoSeed.FactoryB, (await Version(order.OrderId, 2, "factory-b")).FactoryId);
        using var list = await f.Send(HttpMethod.Get, "/api/factory/order-versions?pageSize=100", "factory-a");
        var own = (await list.Content.ReadFromJsonAsync<VersionPage>())!.Items.Where(x => x.OrderId == order.OrderId).ToArray();
        Assert.Single(own); Assert.Equal(1, own[0].Version);
        using var restarted = new ApiFactory(f.Connection, f.Key, new());
        using var client = restarted.CreateClient();
        using var restored = await f.Send(HttpMethod.Get, $"/api/purchase-orders/{order.OrderId}/versions/1", client: client);
        Assert.Equal(history.Lines.ToArray(), (await restored.Content.ReadFromJsonAsync<VersionDetail>())!.Lines.ToArray());
        var accepted = await Write(order.OrderId, "versions/2/accept", new RevisionRequest(5), "factory-b");
        Assert.Equal("Accepted", accepted.OrderStatus); Assert.NotNull(accepted.DecisionId);
    }
    [Fact]
    public async Task Rejection_has_reason_is_final_and_its_original_response_can_be_replayed_after_resubmission()
    {
        var order = await Create(); await Write(order.OrderId, "submissions", new RevisionRequest(1));
        foreach (var reason in new[] { "", "   ", new string('x', 501) })
        {
            using var invalid = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/versions/1/reject", "factory-a",
                new ReasonRequest(2, reason), Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(2, (await Detail(order.OrderId)).Revision); Assert.Equal("Pending", (await Version(order.OrderId, 1)).Status);
        Assert.Equal(2, await f.Db(db => db.Audit.CountAsync(x => x.OrderId == order.OrderId)));
        var key = Guid.NewGuid().ToString(); var request = new ReasonRequest(2, " 无法满足交期 ");
        var original = await Write(order.OrderId, "versions/1/reject", request, "factory-a", key);
        await Write(order.OrderId, "submissions", new RevisionRequest(3));
        Assert.Equal(original, await Write(order.OrderId, "versions/1/reject", request with { Reason = "无法满足交期" }, "factory-a", key));
        foreach (var action in new[] { "accept", "reject", "withdraw" })
        {
            using var repeat = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/versions/1/{action}",
                action == "withdraw" ? "buyer" : "factory-a", new ReasonRequest(4, "另一个决定"), Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        }
        Assert.Equal(4, (await Detail(order.OrderId)).Revision);
    }
    [Theory]
    [InlineData("edit-edit")] [InlineData("edit-submit")] [InlineData("submit-submit")]
    [InlineData("accept-withdraw")] [InlineData("accept-reject")]
    public async Task Concurrent_changes_read_the_same_revision_and_exactly_one_commits(string mode)
    {
        var order = await Create(); var revision = 1;
        if (mode.StartsWith("accept", StringComparison.Ordinal))
        {
            await Write(order.OrderId, "submissions", new RevisionRequest(1)); revision = 2;
        }
        f.Barrier.Arm(order.OrderId);
        var firstKey = Guid.NewGuid().ToString(); var secondKey = Guid.NewGuid().ToString();
        Task<HttpResponseMessage> Send(string action, string key, int quantity) => action switch
        {
            "edit" => f.Send(HttpMethod.Put, $"/api/purchase-orders/{order.OrderId}/draft", body: Edit(revision, quantity), key: key),
            "submit" => f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/submissions", body: new RevisionRequest(revision), key: key),
            _ => f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/versions/1/{action}", action == "withdraw" ? "buyer" : "factory-a",
                new ReasonRequest(revision, "并发决定"), key)
        };
        var actions = mode.Split('-');
        var responses = await Task.WhenAll(Send(actions[0], firstKey, 110), Send(actions[1], secondKey, 120));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();
        var detail = await Detail(order.OrderId); Assert.Equal(revision + 1, detail.Revision);
        Assert.Equal(revision + 1, await f.Db(db => db.Audit.CountAsync(x => x.OrderId == order.OrderId)));
        Assert.Equal(1, await f.Db(db => db.Requests.CountAsync(x => x.Key == firstKey || x.Key == secondKey)));
        Assert.Equal(0, await f.Db(db => db.Requests.CountAsync(x => x.ResponseJson == "")));
        var versions = await f.Db(db => db.Versions.Where(x => x.OrderId == order.OrderId).ToListAsync());
        Assert.True(versions.Count <= 1);
        if (mode.StartsWith("accept", StringComparison.Ordinal))
            Assert.Equal(detail.Status == "Accepted" ? "Accepted" : detail.Status == "Draft" ? "Withdrawn" : "Rejected", versions.Single().Status);
    }
    [Fact]
    public async Task Concurrent_same_key_submission_and_acceptance_each_have_only_one_fact_and_audit()
    {
        var order = await Create();
        async Task<OrderOperationResult[]> Repeat(string suffix, int revision, string user)
        {
            var key = Guid.NewGuid().ToString();
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Send(HttpMethod.Post,
                $"/api/purchase-orders/{order.OrderId}/{suffix}", user, new RevisionRequest(revision), key)));
            var results = new List<OrderOperationResult>();
            foreach (var response in responses)
            {
                using (response) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); results.Add((await response.Content.ReadFromJsonAsync<OrderOperationResult>())!); }
            }
            Assert.Single(results.Distinct()); return results.ToArray();
        }
        await Repeat("submissions", 1, "buyer");
        var accepted = (await Repeat("versions/1/accept", 2, "factory-a"))[0];
        Assert.NotNull(accepted.DecisionId);
        Assert.Equal(1, await f.Db(db => db.Versions.CountAsync(x => x.OrderId == order.OrderId)));
        Assert.Equal(3, await f.Db(db => db.Audit.CountAsync(x => x.OrderId == order.OrderId)));
        using var edit = await f.Send(HttpMethod.Put, $"/api/purchase-orders/{order.OrderId}/draft", body: Edit(3, 140), key: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
    }
    [Theory]
    [InlineData("submissions", "buyer")] [InlineData("versions/1/accept", "factory-a")]
    public async Task Failure_before_commit_rolls_back_all_facts_and_original_key_retry_recovers(string suffix, string user)
    {
        var order = await Create(); var revision = 1;
        if (user != "buyer") { await Write(order.OrderId, "submissions", new RevisionRequest(1)); revision = 2; }
        var key = Guid.NewGuid().ToString(); f.Failure.Armed = 1;
        using var failed = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/{suffix}", user, new RevisionRequest(revision), key);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(revision, (await Detail(order.OrderId)).Revision);
        Assert.Equal(revision, await f.Db(db => db.Audit.CountAsync(x => x.OrderId == order.OrderId)));
        Assert.Equal(0, await f.Db(db => db.Requests.CountAsync(x => x.Key == key)));
        if (revision == 1) Assert.Equal(0, await f.Db(db => db.Versions.CountAsync(x => x.OrderId == order.OrderId)));
        else { var version = await Version(order.OrderId, 1); Assert.Equal("Pending", version.Status); Assert.Null(version.DecisionId); }
        await Write(order.OrderId, suffix, new RevisionRequest(revision), user, key);
    }
    [Fact]
    public async Task Discarded_success_response_can_be_confirmed_after_api_restart_with_original_key()
    {
        var order = await Create(); var key = Guid.NewGuid().ToString();
        using (var discarded = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/submissions", body: new RevisionRequest(1), key: key))
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode); // 客户端不读取第一次成功响应的业务内容。
        using var restarted = new ApiFactory(f.Connection, f.Key, new()); using var client = restarted.CreateClient();
        using var replay = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/submissions", body: new RevisionRequest(1), key: key, client: client);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, (await replay.Content.ReadFromJsonAsync<OrderOperationResult>())!.OrderVersion);
        Assert.Equal(1, await f.Db(db => db.Versions.CountAsync(x => x.OrderId == order.OrderId)));
    }
    [Fact]
    public async Task Past_delivery_stays_draft_and_role_factory_id_and_revision_cannot_be_bypassed()
    {
        var today = ProcurementService.ShanghaiDate(DateTimeOffset.UtcNow);
        var order = await Create(today.AddDays(-1));
        using var old = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/submissions", body: new RevisionRequest(1), key: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode); Assert.Equal(1, (await Detail(order.OrderId)).Revision);
        foreach (var user in new[] { "factory-a", "factory-b", "quality" })
        {
            using var forbidden = await f.Send(HttpMethod.Put, $"/api/purchase-orders/{order.OrderId}/draft", user, Edit(1, 140), Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        var valid = await Create(today); await Write(valid.OrderId, "submissions", new RevisionRequest(1));
        foreach (var user in new[] { "buyer", "quality", "factory-b" })
        {
            using var denied = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{valid.OrderId}/versions/1/accept", user, new RevisionRequest(2), Guid.NewGuid().ToString());
            Assert.Equal(user == "factory-b" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var stale = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{valid.OrderId}/versions/1/accept", "factory-a", new RevisionRequest(1), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var hidden = await f.Send(HttpMethod.Get, $"/api/purchase-orders/{valid.OrderId}/audit", "factory-a");
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
    }
    [Fact]
    public async Task Migration_from_real_1a_schema_preserves_ids_password_and_legacy_idempotent_response()
    {
        const string schema = "migration_upgrade_test";
        var parsed = new NpgsqlConnectionStringBuilder(f.Connection) { SearchPath = schema };
        Assert.EndsWith("_test", parsed.Database!);
        await f.Db(db => db.Database.ExecuteSqlRawAsync("CREATE SCHEMA migration_upgrade_test"));
        try
        {
            await using var db = new ProcurementDbContext(new DbContextOptionsBuilder<ProcurementDbContext>()
                .UseNpgsql(parsed.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
            await db.GetService<IMigrator>().MigrateAsync("20261003111703_InitialDraft");
            var id = Guid.NewGuid(); var factory = Guid.NewGuid(); var sku = Guid.NewGuid(); var lineId = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
            var response = new CreatedOrder(id, "Draft", 1, at); var responseJson = JsonSerializer.Serialize(response);
            var password = Guid.NewGuid().ToString(); var user = new DemoUser("buyer", "buyer", "Buyer", null, "");
            var hashPassword = new PasswordHasher<DemoUser>().HashPassword(user, password);
            var request = new CreateDraftRequest(factory, Future, [new(sku, 100)]);
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO factories ("Id","Name","Active") VALUES ({factory},'Legacy factory',true);
                INSERT INTO skus ("Id","Style","Color","Size","Active") VALUES ({sku},'S001','蓝色','M',true);
                INSERT INTO demo_users ("Id","Username","Role","FactoryId","PasswordHash") VALUES ('buyer','buyer','Buyer',NULL,{hashPassword});
                INSERT INTO purchase_orders ("Id","FactoryId","DeliveryDate","Status","Revision","CreatedBy","CreatedAt") VALUES ({id},{factory},{Future},'Draft',1,'buyer',{at});
                INSERT INTO order_lines ("Id","OrderId","SkuId","Style","Color","Size","Quantity") VALUES ({lineId},{id},{sku},'S001','蓝色','M',100);
                INSERT INTO business_audit ("Id","OrderId","Action","SubjectId","FactoryId","OccurredAt") VALUES ({Guid.NewGuid()},{id},'DraftCreated','buyer',{factory},{at});
                INSERT INTO http_request_results ("SubjectId","Operation","Key","Hash","ResponseJson") VALUES ('buyer','CreateDraft','legacy-key',{hash},{responseJson});
                """);
            await db.Database.MigrateAsync();
            var restored = await db.Orders.Include(x => x.Lines).SingleAsync(x => x.Id == id);
            Assert.Equal(lineId, restored.Lines.Single().Id); Assert.Equal(0, restored.LastSubmittedVersion);
            Assert.Equal(1, (await db.Audit.SingleAsync()).ResultRevision);
            Assert.Equal(response, await new ProcurementStore(db).FindRequest("buyer", "legacy-key", hash, default));
            var restoredUser = await db.Users.SingleAsync();
            Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<DemoUser>().VerifyHashedPassword(restoredUser, restoredUser.PasswordHash, password));
        }
        finally { await f.Db(db => db.Database.ExecuteSqlRawAsync("DROP SCHEMA migration_upgrade_test CASCADE")); }
    }
}
