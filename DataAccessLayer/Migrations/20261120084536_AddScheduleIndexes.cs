using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_enrollments_class_id_user_id",
                table: "enrollments",
                columns: new[] { "class_id", "user_id" },
                filter: "status = 'active'")
                .Annotation("Npgsql:IndexInclude", new[] { "seats" });

            migrationBuilder.CreateIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes",
                columns: new[] { "room_id", "starts_at", "id" },
                filter: "NOT is_cancelled");

            migrationBuilder.CreateIndex(
                name: "IX_classes_starts_at_id",
                table: "classes",
                columns: new[] { "starts_at", "id" },
                filter: "NOT is_cancelled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_enrollments_class_id_user_id",
                table: "enrollments");

            migrationBuilder.DropIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes");

            migrationBuilder.DropIndex(
                name: "IX_classes_starts_at_id",
                table: "classes");
        }
    }
}
