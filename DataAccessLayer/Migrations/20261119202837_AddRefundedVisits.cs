using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundedVisits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "refunded_visits",
                table: "enrollments",
                type: "integer",
                nullable: true);

            // решение: засеянные отменённые брони отменены больше чем за 2 часа до начала, по §3 это полный
            // возврат; cancelled_at не смотрим — у части сидов он позже момента заполнения базы.
            migrationBuilder.Sql("UPDATE enrollments SET refunded_visits = seats WHERE status = 'cancelled'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_enrollments_refunded_visits",
                table: "enrollments",
                sql: "refunded_visits >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_enrollments_refunded_visits_status",
                table: "enrollments",
                sql: "(status = 'active') = (refunded_visits IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_enrollments_refunded_visits",
                table: "enrollments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_enrollments_refunded_visits_status",
                table: "enrollments");

            migrationBuilder.DropColumn(
                name: "refunded_visits",
                table: "enrollments");
        }
    }
}
