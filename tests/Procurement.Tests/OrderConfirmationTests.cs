using Procurement.Application;
using Procurement.Domain;

namespace Procurement.Tests;

public sealed class OrderConfirmationTests
{
    private static readonly DateOnly Today = new(2026, 10, 4);
    private static readonly DateTimeOffset At = new(2026, 10, 4, 4, 0, 0, TimeSpan.Zero);
    private static DraftLine Line(Guid sku, int quantity = 100) => new(sku, new("S001", "蓝色", "M"), PieceQuantity.From(quantity));
    private static PurchaseOrder Create(DateOnly? date = null) => PurchaseOrder.CreateDraft(Guid.NewGuid(), date ?? Today,
        "buyer", At, [Line(Guid.NewGuid())]);

    [Fact]
    public void Edit_preserves_existing_line_and_validation_failure_does_not_partially_change_order()
    {
        var order = Create(); var old = order.Lines.Single();
        var invalid = new DraftLine(Guid.NewGuid(), new("S002", "黑色", "L"), default);
        Assert.Throws<DomainRuleViolation>(() => order.UpdateDraft(order.FactoryId, Today, [Line(old.SkuId, 120), invalid]));
        Assert.Equal(100, old.Quantity.Value); Assert.Equal(1, order.Revision);
        Assert.True(order.UpdateDraft(order.FactoryId, Today, [Line(old.SkuId, 120), Line(Guid.NewGuid(), 50)]));
        Assert.Equal(old.Id, order.Lines.Single(x => x.SkuId == old.SkuId).Id);
        Assert.Equal(2, order.Revision);
        Assert.False(order.UpdateDraft(order.FactoryId, Today, order.Lines.Select(x => Line(x.SkuId, x.Quantity.Value))));
        Assert.Equal(2, order.Revision);
    }
    [Fact]
    public void Hundred_line_limit_is_enforced_by_creation_and_edit_behaviors()
    {
        var lines = Enumerable.Range(0, 101).Select(_ => Line(Guid.NewGuid())).ToArray();
        Assert.Throws<DomainRuleViolation>(() => PurchaseOrder.CreateDraft(Guid.NewGuid(), Today, "buyer", At, lines));
        var order = Create();
        Assert.Throws<DomainRuleViolation>(() => order.UpdateDraft(order.FactoryId, Today, lines));
        Assert.True(order.UpdateDraft(order.FactoryId, Today, lines.Take(100)));
        Assert.Equal(100, order.Lines.Count);
    }
    [Fact]
    public void Withdraw_edit_and_resubmit_preserves_first_snapshot_and_uses_new_version()
    {
        var order = Create(); var line = order.Lines.Single();
        var first = order.Submit("工厂 A", "buyer", At, Today);
        Assert.Throws<DomainConflict>(() => order.UpdateDraft(order.FactoryId, Today, [Line(line.SkuId, 120)]));
        order.WithdrawSubmission(first, "buyer", At, "调整数量");
        order.UpdateDraft(Guid.NewGuid(), Today.AddDays(1), [Line(line.SkuId, 120)]);
        var second = order.Submit("工厂 B", "buyer", At, Today);
        Assert.Equal(100, first.Lines.Single().Quantity.Value); Assert.Equal("工厂 A", first.FactoryName);
        Assert.Equal("Withdrawn", first.Status); Assert.Equal(120, second.Lines.Single().Quantity.Value);
        Assert.Equal(line.Id, second.Lines.Single().LineId); Assert.Equal(2, second.Version);
        Assert.Equal(5, order.Revision);
        Assert.Throws<DomainConflict>(() => order.AcceptVersion(first, first.FactoryId, "factory-a", At));
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public void Rejection_and_withdrawal_require_a_reason_and_leave_pending_unchanged(string? reason)
    {
        var order = Create(); var version = order.Submit("A", "buyer", At, Today);
        Assert.Throws<DomainRuleViolation>(() => order.RejectVersion(version, order.FactoryId, "factory-a", At, reason));
        Assert.Throws<DomainRuleViolation>(() => order.WithdrawSubmission(version, "buyer", At, reason));
        Assert.Equal("Pending", version.Status); Assert.Equal(2, order.Revision); Assert.Null(version.DecisionId);
    }
    [Fact]
    public void Rejected_version_can_be_resubmitted_but_accepted_order_cannot_be_changed()
    {
        var order = Create(); var first = order.Submit("A", "buyer", At, Today);
        Assert.Throws<DomainConflict>(() => order.AcceptVersion(first, Guid.NewGuid(), "wrong-factory", At));
        Assert.Throws<DomainRuleViolation>(() => order.RejectVersion(first, order.FactoryId, "factory-a", At, new string('x', 501)));
        order.RejectVersion(first, order.FactoryId, "factory-a", At, "  交期无法满足  ");
        Assert.Equal("交期无法满足", first.Reason); Assert.NotNull(first.DecisionId);
        var second = order.Submit("A", "buyer", At, Today);
        order.AcceptVersion(second, order.FactoryId, "factory-a", At);
        Assert.Equal(2, order.AcceptedOrderVersion);
        Assert.Throws<DomainConflict>(() => order.RejectVersion(second, order.FactoryId, "factory-a", At, "更改决定"));
        Assert.Throws<DomainConflict>(() => order.WithdrawSubmission(second, "buyer", At, "更改决定"));
        Assert.Throws<DomainConflict>(() => order.Submit("A", "buyer", At, Today));
        Assert.Throws<DomainConflict>(() => order.UpdateDraft(order.FactoryId, Today, [Line(Guid.NewGuid())]));
    }
    [Fact]
    public void Past_delivery_is_allowed_in_draft_but_rejected_on_submission_and_today_is_allowed()
    {
        var old = Create(Today.AddDays(-1));
        Assert.Throws<DomainRuleViolation>(() => old.Submit("A", "buyer", At, Today));
        Assert.Equal("Draft", old.Status); Assert.Equal(1, old.Revision);
        Assert.Equal("Pending", Create().Submit("A", "buyer", At, Today).Status);
    }
    [Fact]
    public void Business_date_changes_at_shanghai_midnight_rather_than_utc_midnight()
    {
        Assert.Equal(Today, ProcurementService.ShanghaiDate(new(2026, 10, 4, 15, 59, 59, TimeSpan.Zero)));
        Assert.Equal(Today.AddDays(1), ProcurementService.ShanghaiDate(new(2026, 10, 4, 16, 0, 0, TimeSpan.Zero)));
    }
}
