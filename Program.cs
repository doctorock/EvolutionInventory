// ============================================================
//  EvolutionInventory — Read inventory for store 005 via the
//  Pastel Evolution SDK (read-only; no writes are performed).
//
//  Auth credentials from evolution\Auth Key.txt:
//    Developer serial : DE12211012
//    SDK Code         : 1824675
//
//  Usage:
//    dotnet run -- --method sql               # direct SQL (default, fastest)
//    dotnet run -- --method sdk               # Pastel InventoryItem API
//    dotnet run -- --method sql --all         # include zero-qty items
//    dotnet run -- --method sdk --debug       # show DataTable schema + null ratio
//    dotnet run -- --method sdk --limit 10    # resolve only first 10 items (SDK probe)
//    dotnet run -- --method sdk --limit 0     # resolve all items (slow on remote server)
//    dotnet run -- --method sql C:\out.txt    # custom output path
// ============================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Pastel.Evolution;

namespace JekyllAndHide.Evolution
{
    internal static class Program
    {
        // -------------------------------------------------------
        //  CONNECTION CONFIG
        // -------------------------------------------------------

        private const string SqlServer    = "41.76.210.14,1433";
        private const string CompanyDb    = "MATT Copy of International Colours CT (Pty) Ltd";
        private const string CommonDb     = "SageCommon";
        private const string DbUser       = "sa";
        private const string DbPassword   = @"G4%$nZyk@w!qd";

        // -------------------------------------------------------
        //  SDK LICENCE (from evolution\Auth Key.txt)
        // -------------------------------------------------------

        private const string SerialNumber = "DE12211012";
        private const string AuthCode     = "1824675";

        // -------------------------------------------------------
        //  TARGET STORE
        // -------------------------------------------------------

        private const string StoreCode = "005";

        // -------------------------------------------------------
        //  ENTRY POINT
        // -------------------------------------------------------

        private static int Main(string[] args)
        {
            // ---- parse flags -----------------------------------------------
            bool showAll   = args.Any(a => a.Equals("--all",   StringComparison.OrdinalIgnoreCase));
            bool debugMode = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));

            // --method sql | sdk   (default: sql)
            string method    = "sql";
            var    argsList  = args.ToList();
            int    methodIdx = argsList.FindIndex(a => a.Equals("--method", StringComparison.OrdinalIgnoreCase));
            if (methodIdx >= 0 && methodIdx + 1 < argsList.Count)
            {
                method = argsList[methodIdx + 1].Trim().ToLowerInvariant();
                argsList.RemoveAt(methodIdx + 1);
                argsList.RemoveAt(methodIdx);
            }
            if (method != "sql" && method != "sdk")
            {
                Console.Error.WriteLine($"Unknown method '{method}'. Use --method sql or --method sdk.");
                return 1;
            }

            // --limit N   (SDK only — cap per-item lookups; 0 = all; default 100)
            int limit    = 100;
            int limitIdx = argsList.FindIndex(a => a.Equals("--limit", StringComparison.OrdinalIgnoreCase));
            if (limitIdx >= 0 && limitIdx + 1 < argsList.Count)
            {
                int.TryParse(argsList[limitIdx + 1], out limit);
                argsList.RemoveAt(limitIdx + 1);
                argsList.RemoveAt(limitIdx);
            }

            // first non-flag arg is optional output path
            string outputPath = argsList.FirstOrDefault(a => !a.StartsWith("--"))
                ?? Path.Combine(
                    AppContext.BaseDirectory,
                    $"evolution_store{StoreCode}_{method}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            // ---- startup banner --------------------------------------------
            var totalTimer = Stopwatch.StartNew();

            Banner();
            Console.WriteLine($"  Store      : {StoreCode}");
            Console.WriteLine($"  Method     : {method.ToUpperInvariant()}");
            Console.WriteLine($"  SQL Server : {SqlServer}");
            Console.WriteLine($"  Company DB : {CompanyDb}");
            Console.WriteLine($"  Common DB  : {CommonDb}");
            Console.WriteLine($"  Serial     : {SerialNumber}");
            Console.WriteLine($"  Output     : {outputPath}");
            Console.WriteLine($"  Zero-qty   : {(showAll ? "included (--all)" : "excluded")}");
            if (method == "sdk")
                Console.WriteLine($"  SDK limit  : {(limit <= 0 ? "all items" : $"{limit} items (--limit N to change)")}");
            Console.WriteLine();

            try
            {
                // ---- SDK init (required by both methods) -------------------
                //  Mandatory order:
                //    1. CreateCommonDBConnection
                //    2. SetLicense
                //    3. CreateConnection

                var sw = Stopwatch.StartNew();
                Console.Write("Connecting to common DB... ");
                DatabaseContext.CreateCommonDBConnection(SqlServer, CommonDb, DbUser, DbPassword, false);
                Console.WriteLine($"OK  ({sw.ElapsedMilliseconds} ms)");

                sw.Restart();
                Console.Write($"Setting licence ({SerialNumber})... ");
                DatabaseContext.SetLicense(SerialNumber, AuthCode);
                Console.WriteLine($"OK  ({sw.ElapsedMilliseconds} ms)");

                sw.Restart();
                Console.Write($"Connecting to company DB... ");
                DatabaseContext.CreateConnection(SqlServer, CompanyDb, DbUser, DbPassword, false);
                Console.WriteLine($"OK  ({sw.ElapsedMilliseconds} ms)\n");

                // ---- locate warehouse --------------------------------------
                sw.Restart();
                Console.Write($"Looking up warehouse '{StoreCode}'... ");
                int whId = Warehouse.FindByCode(StoreCode);
                if (whId < 0) whId = Warehouse.Find(StoreCode);
                if (whId < 0)
                    throw new Exception(
                        $"Warehouse '{StoreCode}' not found. " +
                        "Verify the code in Evolution → Inventory → Warehouses.");

                var warehouse = new Warehouse(whId);
                Console.WriteLine($"OK  ({sw.ElapsedMilliseconds} ms)");
                Console.WriteLine($"  [{warehouse.Code}] {warehouse.Description}  (internal ID: {warehouse.ID})\n");

                // ---- fetch inventory ---------------------------------------
                List<StoreItem> results = method == "sdk"
                    ? ReadInventorySdk(warehouse, showAll, debugMode, limit)
                    : ReadInventorySql(warehouse.ID, showAll, debugMode);

                Console.WriteLine($"  {results.Count} item(s) returned.\n");
                results.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));

                // ---- write output ------------------------------------------
                WriteResults(results, warehouse, method, outputPath, totalTimer.Elapsed);
                return 0;
            }
            catch (EvolutionException ex)
            {
                Console.Error.WriteLine($"\n[Evolution error] {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n[Error] {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
                return 1;
            }
        }

        // ================================================================
        //  METHOD A — Pastel Evolution SDK
        //
        //  Phase 1: InventoryItem.List("") → stock master DataTable
        //           (columns: StockLink, Code, Description_1/2/3,
        //            ServiceItem, ItemActive — NO quantity columns).
        //
        //  Phase 2: For each item, new InventoryItem(stockLink) then
        //           item[warehouseCode] to get per-warehouse quantities.
        //           Each call is one DB round-trip; on a remote server
        //           this is slow. Use --limit N to cap how many items
        //           are resolved (default 100). Pass --limit 0 for all.
        //
        //  Use --debug to dump the stock master DataTable schema.
        // ================================================================

        private static List<StoreItem> ReadInventorySdk(Warehouse warehouse, bool showAll, bool debugMode, int limit)
        {
            // ---- Phase 1: stock master (one batch query) ----------------
            var sw = Stopwatch.StartNew();
            Console.Write("  [SDK] InventoryItem.List() — stock master... ");
            DataTable table = InventoryItem.List("");
            Console.WriteLine($"{table.Rows.Count} rows  ({sw.ElapsedMilliseconds} ms)");

            if (debugMode)
            {
                Console.WriteLine("  [SDK] DataTable columns:");
                foreach (DataColumn col in table.Columns)
                    Console.WriteLine($"    {col.ColumnName}  ({col.DataType.Name})");
                Console.WriteLine();
            }

            int cap = limit <= 0 ? table.Rows.Count : Math.Min(limit, table.Rows.Count);
            Console.WriteLine($"  [SDK] Resolving warehouse [{warehouse.Code}] quantities" +
                              $" for {cap} of {table.Rows.Count} items" +
                              (limit > 0 && limit < table.Rows.Count ? $" (--limit {limit})" : " (all)") +
                              "...");

            // ---- Phase 2: per-item warehouse quantity lookup ------------
            sw.Restart();
            var results      = new List<StoreItem>();
            int resolved     = 0;
            int nullCount    = 0;
            int errorCount   = 0;
            int processed    = 0;
            string firstError = string.Empty;

            foreach (DataRow row in table.Rows)
            {
                if (processed >= cap) break;
                processed++;

                string code = row.IsNull("Code") ? string.Empty : row["Code"].ToString()!;
                if (string.IsNullOrWhiteSpace(code)) continue;

                string desc = row.IsNull("Description_1") ? string.Empty : row["Description_1"].ToString()!;
                int stockLink = row.IsNull("StockLink") ? -1 : Convert.ToInt32(row["StockLink"]);

                if (processed % 10 == 0)
                    Console.Write($"\r  [SDK] {processed}/{cap}...  ");

                InventoryItem? whItem = null;
                try
                {
                    var item = stockLink >= 0
                        ? new InventoryItem(stockLink)
                        : new InventoryItem(code);
                    whItem = item[warehouse.Code];
                }
                catch (EvolutionException ex)
                {
                    errorCount++;
                    if (firstError.Length == 0) firstError = ex.Message;
                    if (showAll) results.Add(new StoreItem(code, desc, 0d, 0d));
                    continue;
                }

                if (whItem == null)
                {
                    nullCount++;
                    if (showAll) results.Add(new StoreItem(code, desc, 0d, 0d));
                    continue;
                }

                resolved++;
                double onHand = whItem.QtyOnHand;
                double free   = whItem.QtyFree;
                if (showAll || onHand != 0d || free != 0d)
                    results.Add(new StoreItem(code, desc, onHand, free));
            }

            Console.WriteLine($"\r  [SDK] Done  ({sw.ElapsedMilliseconds} ms)                    ");
            Console.WriteLine($"  Resolved OK            : {resolved} / {processed}");
            Console.WriteLine($"  Null (no whse record)  : {nullCount} / {processed}");
            Console.WriteLine($"  SDK errors             : {errorCount} / {processed}");
            if (firstError.Length > 0)
                Console.WriteLine($"  First error            : {firstError}");
            if (errorCount == processed && processed > 0)
                Console.WriteLine(
                    "  !! All items failed. This database copy is missing the detail records\n" +
                    "     (_etblStockDetails) that the SDK requires to load individual items.\n" +
                    "     Per-warehouse quantities are unavailable via the SDK on this DB.\n" +
                    "     Use --method sql for accurate inventory data.");
            if (processed < table.Rows.Count)
                Console.WriteLine($"  ({table.Rows.Count - processed} items skipped — use --limit 0 to attempt all)");

            return results;
        }

        // ================================================================
        //  METHOD B — Direct SQL against _bvStockAndWhseItems
        //
        //  Bypasses the SDK object model entirely. Filters by the
        //  warehouse's internal integer ID (WhseID), which the app
        //  resolves via Warehouse.FindByCode() above.
        //
        //  Confirmed column names (from _bvStockAndWhseItems):
        //    WhseID, Code, Description_1, QtyOnHand, QtyAvailable
        // ================================================================

        private static List<StoreItem> ReadInventorySql(int warehouseId, bool showAll, bool debugMode)
        {
            string connStr = $"Data Source={SqlServer};Initial Catalog={CompanyDb};" +
                             $"User ID={DbUser};Password={DbPassword};Connection Timeout=30;";

            var sw = Stopwatch.StartNew();
            Console.Write("  [SQL] Opening connection... ");
            using var conn = new System.Data.SqlClient.SqlConnection(connStr);
            conn.Open();
            Console.WriteLine($"OK  ({sw.ElapsedMilliseconds} ms)");

            if (debugMode)
            {
                // Dump a sample row so you can inspect actual column names
                Console.WriteLine("  [SQL] Sampling one row from _bvStockAndWhseItems...");
                using var schemaCmd = new System.Data.SqlClient.SqlCommand(
                    $"SELECT TOP 1 * FROM _bvStockAndWhseItems WHERE WhseID = {warehouseId}", conn);
                using var schemaReader = schemaCmd.ExecuteReader(CommandBehavior.SchemaOnly);
                var schemaTable = schemaReader.GetSchemaTable();
                if (schemaTable != null)
                    foreach (DataRow r in schemaTable.Rows)
                        Console.WriteLine($"    {r["ColumnName"]}  ({r["DataType"]})");
                Console.WriteLine();
            }

            string sql = @"
                SELECT
                    v.Code,
                    v.Description_1,
                    ISNULL(v.QtyOnHand,    0) AS QtyOnHand,
                    ISNULL(v.QtyAvailable, 0) AS QtyAvailable
                FROM _bvStockAndWhseItems v
                WHERE v.WhseID = @WhseID"
                + (showAll ? "" : @"
                AND (ISNULL(v.QtyOnHand, 0) <> 0 OR ISNULL(v.QtyAvailable, 0) <> 0)")
                + @"
                ORDER BY v.Code";

            sw.Restart();
            Console.Write("  [SQL] Executing query... ");
            using var cmd = new System.Data.SqlClient.SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@WhseID", warehouseId);
            cmd.CommandTimeout = 120;

            using var reader = cmd.ExecuteReader();
            var results = new List<StoreItem>();
            while (reader.Read())
            {
                results.Add(new StoreItem(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Convert.ToDouble(reader["QtyOnHand"]),
                    Convert.ToDouble(reader["QtyAvailable"])));
            }

            Console.WriteLine($"Done  ({sw.ElapsedMilliseconds} ms)");
            return results;
        }

        // ================================================================
        //  OUTPUT
        // ================================================================

        private static void WriteResults(
            List<StoreItem> results,
            Warehouse       warehouse,
            string          method,
            string          outputPath,
            TimeSpan        elapsed)
        {
            const int W_CODE = 25;
            const int W_DESC = 50;
            const int W_QTY  = 14;

            string header  = "ItemCode".PadRight(W_CODE)    + " " +
                             "Description".PadRight(W_DESC) + " " +
                             "QtyOnHand".PadLeft(W_QTY)     + " " +
                             "QtyFree".PadLeft(W_QTY);
            string divider = new string('─', header.Length);
            string stamp   = $"Store {warehouse.Code} ({warehouse.Description})" +
                             $"  |  Method: {method.ToUpperInvariant()}" +
                             $"  |  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" +
                             $"  |  Fetch time: {elapsed.TotalSeconds:F2}s";

            // Console
            Console.WriteLine(stamp);
            Console.WriteLine(divider);
            Console.WriteLine(header);
            Console.WriteLine(divider);
            foreach (var item in results)
                Console.WriteLine(FormatLine(item, W_CODE, W_DESC, W_QTY));
            Console.WriteLine(divider);
            Console.WriteLine($"Total: {results.Count} item(s)  |  Fetch time: {elapsed.TotalSeconds:F2}s");

            // File
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
            writer.WriteLine(stamp);
            writer.WriteLine(divider);
            writer.WriteLine(header);
            writer.WriteLine(divider);
            foreach (var item in results)
                writer.WriteLine(FormatLine(item, W_CODE, W_DESC, W_QTY));
            writer.WriteLine(divider);
            writer.WriteLine($"Total: {results.Count} item(s)  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  |  Fetch time: {elapsed.TotalSeconds:F2}s");

            Console.WriteLine($"\nSaved to: {outputPath}");
        }

        private static string FormatLine(StoreItem item, int wCode, int wDesc, int wQty)
            => item.Code.PadRight(wCode)                     + " " +
               item.Description.PadRight(wDesc)              + " " +
               item.QtyOnHand.ToString("N2").PadLeft(wQty)   + " " +
               item.QtyFree.ToString("N2").PadLeft(wQty);

        private static void Banner()
        {
            Console.WriteLine("╔═══════════════════════════════════════════════╗");
            Console.WriteLine("║  Pastel Evolution — Store Inventory Reader     ║");
            Console.WriteLine("║  Jekyll & Hide  |  READ-ONLY                   ║");
            Console.WriteLine("╚═══════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }

    // ================================================================
    //  Data transfer object — one item's inventory data
    // ================================================================

    internal class StoreItem
    {
        public string Code        { get; }
        public string Description { get; }
        public double QtyOnHand   { get; }
        public double QtyFree     { get; }

        public StoreItem(string code, string description, double qtyOnHand, double qtyFree)
        {
            Code        = code;
            Description = description;
            QtyOnHand   = qtyOnHand;
            QtyFree     = qtyFree;
        }
    }
}
