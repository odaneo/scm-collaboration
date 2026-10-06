using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialProductionTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.ConsumerName, x.MessageId });
                    table.CheckConstraint("ck_inbox_status", "\"Status\" IN ('Pending','Processed','Blocked')");
                });

            migrationBuilder.CreateTable(
                name: "message_failures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_failures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FactId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "production_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedOrderVersion = table.Column<int>(type: "integer", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedRevision = table.Column<int>(type: "integer", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_tasks", x => x.Id);
                    table.CheckConstraint("ck_task_version", "\"AcceptedOrderVersion\" > 0 AND \"AcceptedRevision\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "business_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_business_audit_production_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "production_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_task_lines",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkuId = table.Column<Guid>(type: "uuid", nullable: false),
                    Style = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConfirmedQuantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_task_lines", x => new { x.TaskId, x.LineId });
                    table.CheckConstraint("ck_task_line_quantity", "\"ConfirmedQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_production_task_lines_production_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "production_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_audit_DecisionId_Action",
                table: "business_audit",
                columns: new[] { "DecisionId", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_business_audit_TaskId",
                table: "business_audit",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_inbox_messages_Status_NextAttemptAt",
                table: "inbox_messages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_message_failures_ConsumerName_MessageId_Hash",
                table: "message_failures",
                columns: new[] { "ConsumerName", "MessageId", "Hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_EventType_FactId",
                table: "outbox_messages",
                columns: new[] { "EventType", "FactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_SentAt_NextAttemptAt",
                table: "outbox_messages",
                columns: new[] { "SentAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_task_lines_TaskId_SkuId",
                table: "production_task_lines",
                columns: new[] { "TaskId", "SkuId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_tasks_DecisionId",
                table: "production_tasks",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_tasks_FactoryId_CreatedAtUtc",
                table: "production_tasks",
                columns: new[] { "FactoryId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_production_tasks_OrderId",
                table: "production_tasks",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_tasks_OrderId_AcceptedOrderVersion",
                table: "production_tasks",
                columns: new[] { "OrderId", "AcceptedOrderVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_audit");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "message_failures");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "production_task_lines");

            migrationBuilder.DropTable(
                name: "production_tasks");
        }
    }
}
