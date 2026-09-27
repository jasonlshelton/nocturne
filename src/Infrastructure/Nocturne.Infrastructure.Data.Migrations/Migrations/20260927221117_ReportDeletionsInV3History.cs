using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets the v3 history reads return soft-deleted rows: foods become soft-deletable, and the
    /// history-page indexes lose their <c>deleted_at IS NULL</c> predicate. Each wide index is built
    /// through <see cref="ConcurrentIndexBuilder"/> before the partial one it replaces is dropped, so
    /// history reads stay indexed throughout.
    /// </summary>
    public partial class ReportDeletionsInV3History : Migration
    {
        /// <summary>Mirrors <c>NocturneDbContext.V4HistoryPagedEntities</c>.</summary>
        private static readonly string[] HistoryPagedTables =
        [
            "aps_snapshots",
            "basal_schedules",
            "bg_checks",
            "bolus_calculations",
            "boluses",
            "calibrations",
            "carb_intakes",
            "carb_ratio_schedules",
            "device_events",
            "meter_glucose",
            "notes",
            "sensitivity_schedules",
            "sensor_glucose",
            "target_range_schedules",
            "temp_basals",
            "therapy_settings",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "foods",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "foods",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_history",
                    $"ON {table} (tenant_id, sys_updated_at, id)");
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_sys_updated_at");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_sys_updated_at",
                    $"ON {table} (tenant_id, sys_updated_at, id) WHERE deleted_at IS NULL");
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_history");
            }

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "foods");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "foods");
        }
    }
}
