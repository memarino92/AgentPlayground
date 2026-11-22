using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Api.Automations.Migrations
{
    /// <inheritdoc />
    public partial class DurableWorkflowChildren : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempt",
                schema: "automation",
                table: "Steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ChildId",
                schema: "automation",
                table: "Steps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Deadline",
                schema: "automation",
                table: "Steps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntil",
                schema: "automation",
                table: "Steps",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempt",
                schema: "automation",
                table: "Steps");

            migrationBuilder.DropColumn(
                name: "ChildId",
                schema: "automation",
                table: "Steps");

            migrationBuilder.DropColumn(
                name: "Deadline",
                schema: "automation",
                table: "Steps");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                schema: "automation",
                table: "Steps");
        }
    }
}
