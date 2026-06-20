// ============================================================
//  EvolutionInventory — Read inventory for store 005 via the
//  Pastel Evolution SDK (read-only; no writes are performed).
//
//  Auth credentials from evolution\Auth Key.txt:
//    Developer serial : DE12211012
//    SDK Code         : 1824675
//
//  Usage:
//    dotnet run
//    dotnet run [outputFilePath]
//    dotnet run [outputFilePath] --all        (include zero-qty items)
//    dotnet run [outputFilePath] --debug      (dump DataTable columns)
// ============================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using Pastel.Evolution;

namespace JekyllAndHide.Evolution
{
    internal static class Program
    {
        // -------------------------------------------------------
        //  CONNECTION CONFIG — update before running
        // -------------------------------------------------------

        private const string SqlServer = "(local)";

        /// <summary>
        ///   Your Evolution company database name.
        ///   Check in SQL Server Management Studio or
        ///   Evolution → Utilities → Company Information.
        /// </summary>
        private const string CompanyDb = "EvolutionCompany"; // ← TODO: set your company DB name

        private const string CommonDb   = "EvolutionCommon";
        private const string DbUser     = "";   // empty = Windows integrated auth
        private const string DbPassword = "";

        // -------------------------------------------------------
        //  SDK LICENCE (from evolution\Auth Key.txt)
        // -------------------------------------------------------

        private const string SerialNumber = "DE12211012";
        private const string AuthCode     = "1824675";   // SDK Code

        // -------------------------------------------------------
        //  TARGET STORE
        // -------------------------------------------------------

        private const string StoreCode = "005";

        // -------------------------------------------------------
        //  ENTRY POINT
        // -------------------------------------------------------

        private static int Main(string[] args)
        {
            bool showAll   = args.Any(a => a.Equals("--all",   StringComparison.OrdinalIgnoreCase));
            bool debugMode = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));

            string outputPath = args.FirstOrDefault(a => !a.StartsWith("--"))
                ?? Path.Combine(
                    AppContext.BaseDirectory,
                    $"evolution_store{StoreCode}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            Banner();
            Console.WriteLine($"  Store      : {StoreCode}");
            Console.WriteLine($"  SQL Server : {SqlServer}");
            Console.WriteLine($"  Company DB : {CompanyDb}");
            Console.WriteLine($"  Common DB  : {CommonDb}");
            Console.WriteLine($"  Serial     : {SerialNumber}");
            Console.WriteLine($"  Output     : {outputPath}");
            Console.WriteLine($"  Zero-qty   : {(showAll ? "included (--all)" : "excluded")}");
            Console.WriteLine();

            try
            {
                // ------------------------------------------------
                //  Step 1 — Initialise SDK (mandatory call order)
                //    1. CreateCommonDBConnection
                //    2. SetLicense
                //    3. CreateConnection
                //  All static on DatabaseContext. No writes anywhere.
                // ------------------------------------------------

                Console.Write("Connecting to EvolutionCommon... ");
                DatabaseContext.CreateCommonDBConnection(SqlServer, CommonDb, DbUser, DbPassword, false);
                Console.WriteLine("OK");

                Console.Write($"Setting licence ({SerialNumber})... ");
                DatabaseContext.SetLicense(SerialNumber, AuthCode);
                Console.WriteLine("OK");

                Console.Write($"Connecting to company DB ({CompanyDb})... ");
                DatabaseContext.CreateConnection(SqlServer, CompanyDb, DbUser, DbPassword, false);
                Console.WriteLine("OK\n");

                // ------------------------------------------------
                //  Step 2 — Locate warehouse 005
                // ------------------------------------------------

                Console.Write($"Looking up warehouse '{StoreCode}'... ");
                int whId = Warehouse.FindByCode(StoreCode);

                if (whId < 0)
                    whId = Warehouse.Find(StoreCode); // fallback: search by description

                if (whId < 0)
                    throw new Exception(
                        $"Warehouse '{StoreCode}' not found. " +
                        "Verify the code in Evolution → Inventory → Warehouses.");

                var warehouse = new Warehouse(whId);
                Console.WriteLine($"OK — [{warehouse.Code}] {warehouse.Description}\n");

                // ------------------------------------------------
                //  Step 3 — Read inventory items (read-only)
                //
                //  InventoryItem.List() returns a DataTable of all active items.
                //  For each item we access its WarehouseContext for store 005
                //  via the indexer item[warehouse] — no writes, no Post(), no Save().
                // ------------------------------------------------

                Console.WriteLine("Reading inventory items...");
                DataTable itemTable = InventoryItem.List("Active = 1");

                if (debugMode)
                {
                    Console.WriteLine("[DEBUG] InventoryItem.List columns:");
                    foreach (DataColumn col in itemTable.Columns)
                        Console.WriteLine($"  {col.ColumnName} ({col.DataType.Name})");
                    Console.WriteLine();
                }

                // Detect the item-code column name (SDK versions differ)
                string codeCol = itemTable.Columns.Contains("Code")     ? "Code"
                               : itemTable.Columns.Contains("ItemCode") ? "ItemCode"
                               : itemTable.Columns[0].ColumnName;

                Console.WriteLine($"  {itemTable.Rows.Count} active items found. Fetching store {StoreCode} quantities...\n");

                var results   = new List<StoreItem>();
                int processed = 0;

                foreach (DataRow row in itemTable.Rows)
                {
                    string code = row[codeCol]?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(code)) continue;

                    try
                    {
                        var item = new InventoryItem(code);

                        // item[warehouseCode] returns a warehouse-scoped InventoryItem view.
                        // Quantities and cost on this object are specific to store 005.
                        InventoryItem whItem = item[warehouse.Code];
                        if (whItem == null) continue;

                        double onHand = whItem.QtyOnHand;
                        double free   = whItem.QtyFree;

                        if (!showAll && onHand == 0 && free == 0) continue;

                        results.Add(new StoreItem(
                            item.Code,
                            item.Description ?? string.Empty,
                            onHand,
                            free));
                    }
                    catch (EvolutionException)
                    {
                        // Item not linked to this warehouse — skip
                    }

                    processed++;
                    if (processed % 50 == 0)
                        Console.Write($"\r  Processed {processed}/{itemTable.Rows.Count}...   ");
                }

                Console.WriteLine($"\r  Done. {results.Count} item(s) with stock in store {StoreCode}.\n");
                results.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));

                // ------------------------------------------------
                //  Step 4 — Write output
                // ------------------------------------------------

                WriteResults(results, warehouse, outputPath);
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

        // -------------------------------------------------------
        //  Write formatted results to console + text file
        // -------------------------------------------------------

        private static void WriteResults(List<StoreItem> results, Warehouse warehouse, string outputPath)
        {
            const int W_CODE = 25;
            const int W_DESC = 50;
            const int W_QTY  = 14;

            // Build header using PadRight/PadLeft (variable-width interpolation doesn't compile)
            string header  = "ItemCode".PadRight(W_CODE) + " " +
                             "Description".PadRight(W_DESC) + " " +
                             "QtyOnHand".PadLeft(W_QTY) + " " +
                             "QtyFree".PadLeft(W_QTY);
            string divider = new string('─', header.Length);
            string stamp   = $"Store {warehouse.Code} ({warehouse.Description})  |  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            // Console output
            Console.WriteLine(stamp);
            Console.WriteLine(divider);
            Console.WriteLine(header);
            Console.WriteLine(divider);
            foreach (var item in results)
                Console.WriteLine(FormatLine(item, W_CODE, W_DESC, W_QTY));
            Console.WriteLine(divider);
            Console.WriteLine($"Total: {results.Count} item(s)");

            // File output
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
            writer.WriteLine(stamp);
            writer.WriteLine(divider);
            writer.WriteLine(header);
            writer.WriteLine(divider);
            foreach (var item in results)
                writer.WriteLine(FormatLine(item, W_CODE, W_DESC, W_QTY));
            writer.WriteLine(divider);
            writer.WriteLine($"Total: {results.Count} item(s)  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            Console.WriteLine($"\nSaved to: {outputPath}");
        }

        private static string FormatLine(StoreItem item, int wCode, int wDesc, int wQty)
            => item.Code.PadRight(wCode) + " " +
               item.Description.PadRight(wDesc) + " " +
               item.QtyOnHand.ToString("N2").PadLeft(wQty) + " " +
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

    // -------------------------------------------------------
    //  Value type for one item's inventory data
    // -------------------------------------------------------

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
