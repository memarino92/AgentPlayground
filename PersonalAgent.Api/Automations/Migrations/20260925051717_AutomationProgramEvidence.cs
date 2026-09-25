using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Api.Automations.Migrations
{
    /// <inheritdoc />
    public partial class AutomationProgramEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProgramEvidence",
                schema: "automation",
                table: "Steps",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProgramEvidence",
                schema: "automation",
                table: "Steps");
        }
    }
}
