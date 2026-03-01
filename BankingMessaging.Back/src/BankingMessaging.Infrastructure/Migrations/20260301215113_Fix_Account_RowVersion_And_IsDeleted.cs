using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BankingMessaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Fix_Account_RowVersion_And_IsDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreditTimeoutTokenId",
                table: "TransferStates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DebitTimeoutTokenId",
                table: "TransferStates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                table: "Accounts",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "AccountId",
                keyValue: "ACC-001",
                columns: new string[0],
                values: new object[0]);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "AccountId",
                keyValue: "ACC-002",
                columns: new string[0],
                values: new object[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditTimeoutTokenId",
                table: "TransferStates");

            migrationBuilder.DropColumn(
                name: "DebitTimeoutTokenId",
                table: "TransferStates");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Accounts");

            migrationBuilder.AlterColumn<long>(
                name: "RowVersion",
                table: "Accounts",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "rowversion",
                oldRowVersion: true);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "AccountId",
                keyValue: "ACC-001",
                column: "RowVersion",
                value: 1L);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "AccountId",
                keyValue: "ACC-002",
                column: "RowVersion",
                value: 1L);
        }
    }
}
