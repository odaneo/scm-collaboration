using Microsoft.EntityFrameworkCore;
using Production.Application;
using Production.Domain;

namespace Production.Persistence;

public sealed class ProductionStore(ProductionDbContext db) : IProductionStore
{
    public async Task<(ProductionTask Task, string Hash)?> FindAcceptance(Guid orderId, Guid decisionId, CancellationToken ct)
    {
        var task = await db.Tasks.Include(x => x.Lines).Where(x => x.OrderId == orderId || x.DecisionId == decisionId)
            .OrderByDescending(x => x.OrderId == orderId).FirstOrDefaultAsync(ct);
        return task is null ? null : (task, db.Entry(task).Property<string>("AcceptedContentHash").CurrentValue!);
    }
    public void Add(ProductionTask task, string contentHash)
    { db.Tasks.Add(task); db.Entry(task).Property<string>("AcceptedContentHash").CurrentValue = contentHash; }
    public async Task<TaskPage> List(Guid? factoryId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Tasks.AsNoTracking(); if (factoryId is { } factory) query = query.Where(x => x.FactoryId == factory);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new TaskSummary(x.Id, x.OrderId, x.AcceptedOrderVersion, x.FactoryName, x.DeliveryDate)).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }
    public async Task<TaskDetail?> Detail(Guid? factoryId, Guid id, CancellationToken ct)
    {
        var query = db.Tasks.AsNoTracking(); if (factoryId is { } factory) query = query.Where(x => x.FactoryId == factory);
        var task = await query.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct);
        return task is null ? null : new(task.Id, task.OrderId, task.AcceptedOrderVersion, task.FactoryId, task.FactoryName,
            task.DeliveryDate, task.CreatedAtUtc, task.Lines.Select(x => new TaskLineDetail(x.LineId, x.SkuId, x.Style, x.Color, x.Size, x.ConfirmedQuantity)).ToArray());
    }
}
