using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Procurement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderSubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_business_audit_OrderId_Action",
                table: "business_audit");

            migrationBuilder.AddColumn<int>(
                name: "AcceptedOrderVersion",
                table: "purchase_orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastSubmittedVersion",
                table: "purchase_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Changes",
                table: "business_audit",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderVersion",
                table: "business_audit",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "business_audit",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultRevision",
                table: "business_audit",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "order_versions",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SubmittedRevision = table.Column<int>(type: "integer", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedRevision = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_versions", x => new { x.OrderId, x.Version });
                    table.CheckConstraint("ck_submission_resolution", "(\"Status\" = 'Pending' AND \"ResolvedAt\" IS NULL AND \"ResolvedBy\" IS NULL AND \"ResolvedRevision\" IS NULL AND \"DecisionId\" IS NULL AND \"Reason\" IS NULL) OR (\"Status\" <> 'Pending' AND \"ResolvedAt\" IS NOT NULL AND \"ResolvedBy\" IS NOT NULL AND \"ResolvedRevision\" IS NOT NULL AND \"ResolvedRevision\" > \"SubmittedRevision\" AND ((\"Status\" = 'Withdrawn' AND \"DecisionId\" IS NULL) OR (\"Status\" IN ('Accepted','Rejected') AND \"DecisionId\" IS NOT NULL)) AND (\"Status\" = 'Accepted' OR (\"Reason\" IS NOT NULL AND length(btrim(\"Reason\")) > 0)))");
                    table.CheckConstraint("ck_submission_status", "\"Status\" IN ('Pending','Accepted','Rejected','Withdrawn')");
                    table.CheckConstraint("ck_submission_version", "\"Version\" > 0 AND \"SubmittedRevision\" > 0");
                    table.ForeignKey(
                        name: "FK_order_versions_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_versions_purchase_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "purchase_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_version_lines",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SkuId = table.Column<Guid>(type: "uuid", nullable: false),
                    Style = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_version_lines", x => new { x.OrderId, x.Version, x.LineId });
                    table.CheckConstraint("ck_version_line_quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_order_version_lines_order_versions_OrderId_Version",
                        columns: x => new { x.OrderId, x.Version },
                        principalTable: "order_versions",
                        principalColumns: new[] { "OrderId", "Version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status",
                table: "purchase_orders",
                sql: "\"Status\" IN ('Draft','Submitted','Accepted','Rejected')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_version",
                table: "purchase_orders",
                sql: "\"LastSubmittedVersion\" >= 0 AND (\"AcceptedOrderVersion\" IS NULL OR (\"AcceptedOrderVersion\" > 0 AND \"AcceptedOrderVersion\" <= \"LastSubmittedVersion\"))");

            migrationBuilder.CreateIndex(
                name: "IX_business_audit_OrderId_ResultRevision_Action",
                table: "business_audit",
                columns: new[] { "OrderId", "ResultRevision", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_version_lines_OrderId_Version_SkuId",
                table: "order_version_lines",
                columns: new[] { "OrderId", "Version", "SkuId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_versions_DecisionId",
                table: "order_versions",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_versions_FactoryId_SubmittedAt_OrderId_Version",
                table: "order_versions",
                columns: new[] { "FactoryId", "SubmittedAt", "OrderId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_order_versions_OrderId",
                table: "order_versions",
                column: "OrderId",
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_version_lines");

            migrationBuilder.DropTable(
                name: "order_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status",
                table: "purchase_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_version",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_business_audit_OrderId_ResultRevision_Action",
                table: "business_audit");

            migrationBuilder.DropColumn(
                name: "AcceptedOrderVersion",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "LastSubmittedVersion",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "Changes",
                table: "business_audit");

            migrationBuilder.DropColumn(
                name: "OrderVersion",
                table: "business_audit");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "business_audit");

            migrationBuilder.DropColumn(
                name: "ResultRevision",
                table: "business_audit");

            migrationBuilder.CreateIndex(
                name: "IX_business_audit_OrderId_Action",
                table: "business_audit",
                columns: new[] { "OrderId", "Action" },
                unique: true);
        }
    }
}
