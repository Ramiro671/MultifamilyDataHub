using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814

namespace MDH.OrchestrationService.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialWarehouseSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "warehouse");

        migrationBuilder.CreateTable(
            name: "dim_submarket",
            schema: "warehouse",
            columns: table => new
            {
                SubmarketId = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                State = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Region = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_dim_submarket", x => x.SubmarketId));

        migrationBuilder.CreateTable(
            name: "dim_listing",
            schema: "warehouse",
            columns: table => new
            {
                ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                SubmarketId = table.Column<int>(type: "int", nullable: false),
                StreetAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Bedrooms = table.Column<int>(type: "int", nullable: false),
                Bathrooms = table.Column<decimal>(type: "decimal(4,1)", nullable: false),
                Sqft = table.Column<int>(type: "int", nullable: false),
                Operator = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_dim_listing", x => x.ListingId);
                table.ForeignKey(
                    name: "FK_dim_listing_dim_submarket_SubmarketId",
                    column: x => x.SubmarketId,
                    principalSchema: "warehouse",
                    principalTable: "dim_submarket",
                    principalColumn: "SubmarketId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "fact_daily_rent",
            schema: "warehouse",
            columns: table => new
            {
                FactId = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RentDate = table.Column<DateOnly>(type: "date", nullable: false),
                AskingRent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                EffectiveRent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                Concessions = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                RentPerSqft = table.Column<decimal>(type: "decimal(6,2)", nullable: false),
                LoadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_fact_daily_rent", x => x.FactId);
                table.ForeignKey(
                    name: "FK_fact_daily_rent_dim_listing_ListingId",
                    column: x => x.ListingId,
                    principalSchema: "warehouse",
                    principalTable: "dim_listing",
                    principalColumn: "ListingId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "fact_market_metrics",
            schema: "warehouse",
            columns: table => new
            {
                MetricId = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                SubmarketId = table.Column<int>(type: "int", nullable: false),
                Bedrooms = table.Column<int>(type: "int", nullable: false),
                MetricDate = table.Column<DateOnly>(type: "date", nullable: false),
                AvgRent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                MedianRent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                RentPerSqft = table.Column<decimal>(type: "decimal(6,2)", nullable: false),
                OccupancyEstimate = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                SampleSize = table.Column<int>(type: "int", nullable: false),
                ComputedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_fact_market_metrics", x => x.MetricId);
                table.ForeignKey(
                    name: "FK_fact_market_metrics_dim_submarket_SubmarketId",
                    column: x => x.SubmarketId,
                    principalSchema: "warehouse",
                    principalTable: "dim_submarket",
                    principalColumn: "SubmarketId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "fact_anomaly",
            schema: "warehouse",
            columns: table => new
            {
                AnomalyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AskingRent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                SubmarketAvgRent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                StdDev = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                ZScore = table.Column<decimal>(type: "decimal(6,3)", nullable: false),
                FlagReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IsResolved = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_fact_anomaly", x => x.AnomalyId);
                table.ForeignKey(
                    name: "FK_fact_anomaly_dim_listing_ListingId",
                    column: x => x.ListingId,
                    principalSchema: "warehouse",
                    principalTable: "dim_listing",
                    principalColumn: "ListingId",
                    onDelete: ReferentialAction.Cascade);
            });

        // Seed submarkets
        migrationBuilder.InsertData(
            schema: "warehouse",
            table: "dim_submarket",
            columns: ["SubmarketId", "Name", "State", "Region", "CreatedAt"],
            values: new object[,]
            {
                { 1,  "Austin",    "TX", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 2,  "Houston",   "TX", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 3,  "Dallas",    "TX", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 4,  "Phoenix",   "AZ", "Mountain West",       new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 5,  "Atlanta",   "GA", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 6,  "Denver",    "CO", "Mountain West",       new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 7,  "Miami",     "FL", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 8,  "Nashville", "TN", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 9,  "Tampa",     "FL", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 10, "Orlando",   "FL", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 11, "Raleigh",   "NC", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { 12, "Charlotte", "NC", "Southeast/Southwest", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
            });

        // Indexes
        migrationBuilder.CreateIndex(name: "IX_dim_listing_ExternalId", schema: "warehouse", table: "dim_listing", column: "ExternalId");
        migrationBuilder.CreateIndex(name: "IX_dim_listing_SubmarketId", schema: "warehouse", table: "dim_listing", column: "SubmarketId");
        migrationBuilder.CreateIndex(name: "IX_fact_daily_rent_ListingId_RentDate", schema: "warehouse", table: "fact_daily_rent", columns: ["ListingId", "RentDate"], unique: true);
        migrationBuilder.CreateIndex(name: "IX_fact_market_metrics_SubmarketId_Bedrooms_MetricDate", schema: "warehouse", table: "fact_market_metrics", columns: ["SubmarketId", "Bedrooms", "MetricDate"], unique: true);
        migrationBuilder.CreateIndex(name: "IX_fact_anomaly_ListingId", schema: "warehouse", table: "fact_anomaly", column: "ListingId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "fact_anomaly", schema: "warehouse");
        migrationBuilder.DropTable(name: "fact_daily_rent", schema: "warehouse");
        migrationBuilder.DropTable(name: "fact_market_metrics", schema: "warehouse");
        migrationBuilder.DropTable(name: "dim_listing", schema: "warehouse");
        migrationBuilder.DropTable(name: "dim_submarket", schema: "warehouse");
    }
}
