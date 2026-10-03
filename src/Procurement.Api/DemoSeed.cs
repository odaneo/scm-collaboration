using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Procurement.Application;
using Procurement.Persistence;

namespace Procurement.Api;

public static class DemoSeed
{
    public static readonly Guid FactoryA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid FactoryB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid SkuM = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid SkuL = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid SkuBlackM = Guid.Parse("20000000-0000-0000-0000-000000000003");
    public static async Task Run(ProcurementDbContext db, PasswordHasher<DemoUser> hasher, string password)
    {
        if (password.Length < 12) throw new InvalidOperationException("演示密码至少需要 12 个字符。");
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var factory in new[] { new Factory(FactoryA, "合作工厂 A"), new Factory(FactoryB, "合作工厂 B") })
            if (!await db.Factories.AnyAsync(x => x.Id == factory.Id)) db.Factories.Add(factory);
        foreach (var sku in new[] { new Sku(SkuM, "S001", "蓝色", "M"), new Sku(SkuL, "S001", "蓝色", "L"), new Sku(SkuBlackM, "S002", "黑色", "M") })
            if (!await db.Skus.AnyAsync(x => x.Id == sku.Id)) db.Skus.Add(sku);
        await db.SaveChangesAsync();
        foreach (var user in new[]
        {
            new DemoUser("buyer", "buyer", Roles.Buyer, null, ""),
            new DemoUser("quality", "quality", Roles.Quality, null, ""),
            new DemoUser("factory-a", "factory-a", Roles.Factory, FactoryA, ""),
            new DemoUser("factory-b", "factory-b", Roles.Factory, FactoryB, "")
        })
            if (!await db.Users.AnyAsync(x => x.Id == user.Id)) db.Users.Add(user with { PasswordHash = hasher.HashPassword(user, password) });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
