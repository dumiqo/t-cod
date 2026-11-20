using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlistIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_keys_operation",
                table: "idempotency_keys");

            migrationBuilder.AddColumn<int>(
                name: "result_position",
                table: "idempotency_keys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_keys_operation",
                table: "idempotency_keys",
                sql: "operation IN ('enroll', 'join-waitlist')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_keys_operation",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "result_position",
                table: "idempotency_keys");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_keys_operation",
                table: "idempotency_keys",
                sql: "operation IN ('enroll')");
        }
    }
}
