using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Procurement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "http_request_results",
                columns: table => new
                {
                    SubjectId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResponseJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_http_request_results", x => new { x.SubjectId, x.Operation, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "skus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Style = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skus", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "demo_users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PasswordHash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_demo_users_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_orders", x => x.Id);
                    table.CheckConstraint("ck_order_revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_purchase_orders_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "business_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_business_audit_purchase_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "purchase_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SkuId = table.Column<Guid>(type: "uuid", nullable: false),
                    Style = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => x.Id);
                    table.CheckConstraint("ck_line_quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_order_lines_purchase_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "purchase_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_order_lines_skus_SkuId",
                        column: x => x.SkuId,
                        principalTable: "skus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_audit_OrderId_Action",
                table: "business_audit",
                columns: new[] { "OrderId", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_demo_users_FactoryId",
                table: "demo_users",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_demo_users_Username",
                table: "demo_users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_OrderId_SkuId",
                table: "order_lines",
                columns: new[] { "OrderId", "SkuId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_SkuId",
                table: "order_lines",
                column: "SkuId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_CreatedAt_Id",
                table: "purchase_orders",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_FactoryId",
                table: "purchase_orders",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_skus_Style_Color_Size",
                table: "skus",
                columns: new[] { "Style", "Color", "Size" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_audit");

            migrationBuilder.DropTable(
                name: "demo_users");

            migrationBuilder.DropTable(
                name: "http_request_results");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "purchase_orders");

            migrationBuilder.DropTable(
                name: "skus");

            migrationBuilder.DropTable(
                name: "factories");
        }
    }
}
