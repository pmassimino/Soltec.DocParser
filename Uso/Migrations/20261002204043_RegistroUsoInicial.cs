using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soltec.DocParser.Uso.Migrations
{
    /// <inheritdoc />
    public partial class RegistroUsoInicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegistrosUso",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IdUsuario = table.Column<int>(type: "INTEGER", nullable: false),
                    NombreCliente = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TipoArchivo = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    NombreArchivo = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    TamanoBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Paginas = table.Column<int>(type: "INTEGER", nullable: true),
                    Resultado = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    UsoIa = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProveedorIa = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    DuracionMs = table.Column<int>(type: "INTEGER", nullable: false),
                    TokensEntrada = table.Column<long>(type: "INTEGER", nullable: false),
                    TokensSalida = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoIaUsd = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosUso", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrosUsoIa",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RegistroUsoId = table.Column<long>(type: "INTEGER", nullable: false),
                    Proveedor = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Modelo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokensEntrada = table.Column<int>(type: "INTEGER", nullable: false),
                    TokensEntradaCache = table.Column<int>(type: "INTEGER", nullable: false),
                    TokensSalida = table.Column<int>(type: "INTEGER", nullable: false),
                    CostoUsd = table.Column<decimal>(type: "TEXT", nullable: false),
                    Exitosa = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosUsoIa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrosUsoIa_RegistrosUso_RegistroUsoId",
                        column: x => x.RegistroUsoId,
                        principalTable: "RegistrosUso",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosUso_FechaUtc",
                table: "RegistrosUso",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosUso_IdUsuario_FechaUtc",
                table: "RegistrosUso",
                columns: new[] { "IdUsuario", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosUsoIa_RegistroUsoId",
                table: "RegistrosUsoIa",
                column: "RegistroUsoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrosUsoIa");

            migrationBuilder.DropTable(
                name: "RegistrosUso");
        }
    }
}
