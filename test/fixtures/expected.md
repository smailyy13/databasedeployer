# Expected result — dev.sql vs prod.sql

Run with `--source` = `SchemaDiff_Dev`, `--target` = `SchemaDiff_Prod`.

## Must be reported (5 differences)

| Kind | Object | Changed part |
|---|---|---|
| Added | `SCHEMA [staging]` | — |
| Added | `Procedure [dbo].[NewFeature]` | — |
| Removed | `Procedure [dbo].[LegacyProc]` | — |
| Changed | `Table [dbo].[Customer]` | `columns` (Phone), `indexes` (INCLUDE) |
| Changed | `Procedure [dbo].[GetOrderTotal]` | `body` |

## Must NOT be reported — these are the false-positive traps

| Object | Why it must stay silent |
|---|---|
| `Procedure [dbo].[GetCustomer]` | Identical code, different formatting + comments. Proves the ScriptDom token normalizer works. |
| `dbo.Customer.IsActive` default | System-named (`DF__Customer__IsAct__…`), auto name differs per database. Must be excluded by default. |
| `Table [sales].[Order]` | Identical, including FK and CHECK constraints. |
| `Trigger [dbo].[trg_Customer_Audit]` | Identical. |
| `Sequence [dbo].[OrderNumber]` | Identical — and `current_value` must not be compared. |
| `Synonym [dbo].[CustomerAlias]` | Identical base object; `base_object_name` must not carry the database name. |
| `Function [dbo].[FormatName]` | Identical. |
| `View [dbo].[vwActiveCustomer]` | Identical. |

A false positive here is as much a failure as a missed difference: an engineer who
sees noise in the report stops trusting the tool and goes back to SSDT.
