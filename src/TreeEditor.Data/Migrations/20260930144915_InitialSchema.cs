using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TreeEditor.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ancestors = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    value = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_nodes_parent_id",
                        column: x => x.parent_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "seed_nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ancestors = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    value = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seed_nodes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_nodes_ancestors",
                table: "nodes",
                column: "ancestors")
                .Annotation("Npgsql:IndexMethod", "gin");

            // Expression indexes on lower(value) cannot be modelled in EF Core.
            migrationBuilder.Sql(
                "CREATE INDEX ix_nodes_parent_id_lower_value_id ON nodes (parent_id, lower(value), id);");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_nodes_parent_id_lower_value ON nodes (parent_id, lower(value)) NULLS NOT DISTINCT WHERE NOT is_deleted;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ux_nodes_parent_id_lower_value;");
            migrationBuilder.Sql("DROP INDEX ix_nodes_parent_id_lower_value_id;");

            migrationBuilder.DropTable(
                name: "nodes");

            migrationBuilder.DropTable(
                name: "seed_nodes");
        }
    }
}
