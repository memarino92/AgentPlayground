using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PersonalAgent.Api.Automations.Migrations;

[DbContext(typeof(AutomationDbContext))]
[Migration("20260927090000_CoachCallWorkflow")]
public partial class CoachCallWorkflow : Migration
{
    protected override void Up(MigrationBuilder Migration)
    {
        Migration.CreateTable(
            name: "CoachCallWorkflows",
            schema: "automation",
            columns: Table => new
            {
                CorrelationId = Table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = Table.Column<Guid>(type: "uuid", nullable: false),
                ProviderCorrelationId = Table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = Table.Column<string>(type: "text", nullable: false),
                CurrentState = Table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAt = Table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            }, constraints: Table => Table.PrimaryKey("PK_CoachCallWorkflows", X => X.CorrelationId));
        Migration.CreateIndex("IX_CoachCallWorkflows_ProfileId_UpdatedAt", "CoachCallWorkflows",
            new[] { "ProfileId", "UpdatedAt" }, schema: "automation");
    }

    protected override void Down(MigrationBuilder Migration) => Migration.DropTable("CoachCallWorkflows", "automation");
}
