using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlistEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('enrollments_id_seq'::regclass)"),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waitlist_entries", x => x.id);
                    table.CheckConstraint("ck_waitlist_entries_seats", "seats >= 1");
                    table.CheckConstraint("ck_waitlist_entries_status", "status IN ('waiting', 'cancelled', 'promoted')");
                    table.ForeignKey(
                        name: "FK_waitlist_entries_classes_class_id",
                        column: x => x.class_id,
                        principalTable: "classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_class_id",
                table: "waitlist_entries",
                column: "class_id");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_class_id_id",
                table: "waitlist_entries",
                columns: new[] { "class_id", "id" },
                filter: "status = 'waiting'");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_class_id_user_id",
                table: "waitlist_entries",
                columns: new[] { "class_id", "user_id" },
                unique: true,
                filter: "status = 'waiting'");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_user_id",
                table: "waitlist_entries",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "waitlist_entries");
        }
    }
}
