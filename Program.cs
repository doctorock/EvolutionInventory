// ============================================================
//  EvolutionInventory — Read inventory and create sales orders
//  via the Pastel Evolution SDK (read-only inventory queries;
//  sales-order writes require appropriate Evolution permissions).
//
//  Auth credentials from evolution\Auth Key.txt:
//    Developer serial : DE12211012
//    SDK Code         : 1824675
//
//  Usage — inventory:
//    dotnet run -- --method sql                          # all warehouses, stock only (default)
//    dotnet run -- --method sql --all                    # all warehouses, include zero-qty
//    dotnet run -- --method sql --warehouse 005          # single warehouse
//    dotnet run -- --method sql --warehouse 005 --all    # single warehouse, include zero-qty
//    dotnet run -- --method sql --warehouse 005 C:\out.txt  # custom output path (single only)
//    dotnet run -- --method sdk --warehouse 005          # SDK path (single warehouse only)
//    dotnet run -- --method sdk --warehouse 005 --limit 10  # SDK probe, first 10 items
//    dotnet run -- --method sdk --warehouse 005 --limit 0   # SDK, all items (slow on remote)
//    dotnet run -- --method sdk --warehouse 005 --debug  # show DataTable schema
//
//  Usage — sales orders:
//    dotnet run -- --method order --customer CUST001 --item ITEM001 --qty 5 --price 100.00
//                                             # save as order (no GL posting)
//    dotnet run -- --method order --customer CUST001 --item ITEM001 --qty 5 --price 100.00 --process
//                                             # process directly into an invoice
//    dotnet run -- --method order ... --warehouse 001
//                                             # order from a specific warehouse (default: 005)
//    dotnet run -- --method order ... --rep REP001
//                                             # assign a sales representative to the order
//
//  Usage — JH inventory snapshot:
//    dotnet run -- --method snapshot --env nonprod       # read copy Evolution DB → write to interna1_non_production
//    dotnet run -- --method snapshot --env prod          # read live Evolution DB → write to interna1_b2b
//                                             # writes JHInventory_Snapshot + promotes to JHInventory_Snapshot_Golden
//                                             # also saves a text file to the app directory
// ============================================================

// Code notes 26 July 2026



//


using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
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
        //  FALLBACK WAREHOUSE (order mode only; inventory defaults to "all")
        // -------------------------------------------------------

        private const string DefaultOrderWarehouse = "005";

        // Live Evolution company DB — used by snapshot --env prod
        // (copy DB is used for --env nonprod via the existing CompanyDb constant)
        private const string CompanyDbLive = "International Colours CT (Pty) Ltd";

        // JH store codes that map to Evolution warehouses for the inventory snapshot
        private static readonly string[] JhStoreCodes = { "001", "004", "005", "006", "007", "008", "009" };

        // -------------------------------------------------------
        //  JH WEB DATABASE CONNECTION STRINGS
        //  NOTE: verify prod string before first live run
        // -------------------------------------------------------

        private const string JhConnNonProd =
            "Data Source=148.251.89.211,1433;Network Library=DBMSSOCN;" +
            "Initial Catalog=interna1_non_production;User ID=interna1_non_production;" +
            "Password=x6F@21vh;Connection Timeout=15;" +
            "Connection Lifetime=0;Min Pool Size=0;Max Pool Size=100;Pooling=true;";

        private const string JhConnProd =
            "Data Source=148.251.89.211,1433;Network Library=DBMSSOCN;" +
            "Initial Catalog=interna1_b2b;User ID=interna1_user_dev;" +
            "Password=Sqlserver1433!;Connection Timeout=15;" +
            "Connection Lifetime=0;Min Pool Size=0;Max Pool Size=100;Pooling=true;";

        // -------------------------------------------------------
        //  ENTRY POINT
        // -------------------------------------------------------

        private static int Main(string[] args)
        {
            // ---- parse flags -----------------------------------------------
            bool showAll   = args.Any(a => a.Equals("--all",   StringComparison.OrdinalIgnoreCase));
            bool debugMode = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));

            // --method sql | sdk | order   (default: sql)
            string method    = "sql";
            var    argsList  = args.ToList();
            int    methodIdx = argsList.FindIndex(a => a.Equals("--method", StringComparison.OrdinalIgnoreCase));
            if (methodIdx >= 0 && methodIdx + 1 < argsList.Count)
            {
                method = argsList[methodIdx + 1].Trim().ToLowerInvariant();
                argsList.RemoveAt(methodIdx + 1);
                argsList.RemoveAt(methodIdx);
            }
            if (method != "sql" && method != "sdk" && method != "order" && method != "snapshot")
            {
                Console.Error.WriteLine($"Unknown method '{method}'. Use --method sql, --method sdk, --method order, or --method snapshot.");
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

            // --warehouse: inventory mode → warehouse code or "all" (default)
            //             order mode   → warehouse to order from (default DefaultOrderWarehouse)
            string warehouseArg   = PopArg(argsList, "--warehouse");
            string customerCode   = PopArg(argsList, "--customer");
            string itemCode       = PopArg(argsList, "--item");
            string repCode        = PopArg(argsList, "--rep");
            string envArg         = PopArg(argsList, "--env");
            string orderWarehouse = string.IsNullOrWhiteSpace(warehouseArg) ? DefaultOrderWarehouse : warehouseArg;
            string invWarehouse   = string.IsNullOrWhiteSpace(warehouseArg) ? "all" : warehouseArg;
            bool   allWarehouses  = invWarehouse.Equals("all", StringComparison.OrdinalIgnoreCase);
            bool   processNow     = argsList.RemoveAll(a => a.Equals("--process", StringComparison.OrdinalIgnoreCase)) > 0;
            double orderQty       = 1.0;
            double orderPrice     = 0.0;
            { if (double.TryParse(PopArg(argsList, "--qty"),   System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) orderQty   = v; }
            { if (double.TryParse(PopArg(argsList, "--price"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) orderPrice = v; }

            if (method == "order")
            {
                if (string.IsNullOrWhiteSpace(customerCode))
                { Console.Error.WriteLine("--customer CODE is required for --method order."); return 1; }
                if (string.IsNullOrWhiteSpace(itemCode))
                { Console.Error.WriteLine("--item CODE is required for --method order."); return 1; }
            }

            if (method == "snapshot")
            {
                if (string.IsNullOrWhiteSpace(envArg) ||
                    (!envArg.Equals("nonprod", StringComparison.OrdinalIgnoreCase) &&
                     !envArg.Equals("prod",    StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine("--env nonprod|prod is required for --method snapshot.");
                    return 1;
                }
            }

            // Output path: single-warehouse only; "all" mode generates one file per warehouse.
            string customPath = argsList.FirstOrDefault(a => !a.StartsWith("--")) ?? string.Empty;
            string dateStamp  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outputPath = allWarehouses ? string.Empty
                : (customPath.Length > 0 ? customPath
                   : Path.Combine(AppContext.BaseDirectory,
                       $"evolution_{invWarehouse}_{method}_{dateStamp}.txt"));

            // ---- startup banner --------------------------------------------
            var totalTimer = Stopwatch.StartNew();

            Banner();
            Console.WriteLine($"  Method     : {method.ToUpperInvariant()}");
            Console.WriteLine($"  SQL Server : {SqlServer}");
            Console.WriteLine($"  Company DB : {CompanyDb}");
            Console.WriteLine($"  Common DB  : {CommonDb}");
            Console.WriteLine($"  Serial     : {SerialNumber}");
            if (method == "order")
            {
                Console.WriteLine($"  Customer   : {customerCode}");
                Console.WriteLine($"  Item       : {itemCode}");
                Console.WriteLine($"  Warehouse  : {orderWarehouse}");
                Console.WriteLine($"  Quantity   : {orderQty}");
                Console.WriteLine($"  Price      : {orderPrice:F2}");
                Console.WriteLine($"  Action     : {(processNow ? "process → invoice" : "save as order")}");
                if (!string.IsNullOrWhiteSpace(repCode))
                    Console.WriteLine($"  Sales Rep  : {repCode}");
            }
            else if (method == "snapshot")
            {
                Console.WriteLine($"  Env        : {envArg}");
                Console.WriteLine($"  Target DB  : {(envArg.Equals("prod", StringComparison.OrdinalIgnoreCase) ? "interna1_b2b (PROD)" : "interna1_b2b (nonprod)")}");
            }
            else
            {
                Console.WriteLine($"  Warehouse  : {(allWarehouses ? "all" : invWarehouse)}");
                if (!allWarehouses) Console.WriteLine($"  Output     : {outputPath}");
                Console.WriteLine($"  Zero-qty   : {(showAll ? "included (--all)" : "excluded")}");
                if (method == "sdk")
                    Console.WriteLine($"  SDK limit  : {(limit <= 0 ? "all items" : $"{limit} items (--limit N to change)")}");
            }
            Console.WriteLine();

            try
            {
                // ---- snapshot (no SDK needed — direct JH web DB write) ---
                if (method == "snapshot")
                {
                    UpdateJHInventorySnapshot(envArg);
                    Console.WriteLine($"\nDone.  Total time: {totalTimer.Elapsed.TotalSeconds:F2}s");
                    return 0;
                }

                // ---- SDK init (required by all methods) -------------------
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

                // ---- sales order (exits before warehouse lookup) -----------
                if (method == "order")
                {
                    string reference = CreateSalesOrder(customerCode, itemCode, orderWarehouse, orderQty, orderPrice, processNow, repCode);
                    Console.WriteLine($"\nDone.  Reference: {reference}");
                    Console.WriteLine($"Total time: {totalTimer.Elapsed.TotalSeconds:F2}s");
                    return 0;
                }

                // ---- locate warehouse(s) for inventory methods ------------
                List<Warehouse> warehouses;
                if (allWarehouses)
                {
                    sw.Restart();
                    Console.Write("Loading warehouses... ");
                    warehouses = LoadAllWarehouses();
                    Console.WriteLine($"{warehouses.Count} active  ({sw.ElapsedMilliseconds} ms)\n");
                }
                else
                {
                    sw.Restart();
                    Console.Write($"Looking up warehouse '{invWarehouse}'... ");
                    int whId = Warehouse.FindByCode(invWarehouse);
                    if (whId < 0) whId = Warehouse.Find(invWarehouse);
                    if (whId < 0)
                        throw new Exception(
                            $"Warehouse '{invWarehouse}' not found. " +
                            "Verify the code in Evolution → Inventory → Warehouses.");
                    var wh = new Warehouse(whId);
                    Console.WriteLine($"OK  ({sw.ElapsedMilliseconds} ms)");
                    Console.WriteLine($"  [{wh.Code}] {wh.Description}  (internal ID: {wh.ID})\n");
                    warehouses = [wh];
                }

                // ---- fetch and write per warehouse ------------------------
                var summary = new List<(string Code, string Desc, int Count)>();

                foreach (var warehouse in warehouses)
                {
                    if (allWarehouses)
                    {
                        Console.WriteLine($"  [{warehouse.Code}] {warehouse.Description}");
                        Console.WriteLine($"  {new string('─', 60)}");
                    }

                    List<StoreItem> results = method == "sdk"
                        ? ReadInventorySdk(warehouse, showAll, debugMode, limit)
                        : ReadInventorySql(warehouse.ID, showAll, debugMode);

                    Console.WriteLine($"  {results.Count} item(s) returned.\n");
                    results.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));

                    string filePath = allWarehouses
                        ? Path.Combine(AppContext.BaseDirectory,
                            $"evolution_{warehouse.Code}_{method}_{dateStamp}.txt")
                        : outputPath;

                    WriteResults(results, warehouse, method, filePath, totalTimer.Elapsed);
                    summary.Add((warehouse.Code, warehouse.Description, results.Count));
                }

                // ---- summary table for multi-warehouse runs ---------------
                if (allWarehouses)
                {
                    int totalItems = summary.Sum(s => s.Count);
                    Console.WriteLine($"\n{"Code",-6}  {"Warehouse",-42}  {"Items",6}");
                    Console.WriteLine(new string('─', 58));
                    foreach (var (code, desc, count) in summary)
                        Console.WriteLine($"{code,-6}  {desc,-42}  {count,6}");
                    Console.WriteLine(new string('─', 58));
                    Console.WriteLine($"{"Total",-6}  {warehouses.Count + " warehouse(s)",-42}  {totalItems,6}");
                    Console.WriteLine($"\nFiles written to: {AppContext.BaseDirectory}");
                }

                return 0;
            }
            catch (EvolutionException ex)
            {
                Console.Error.WriteLine($"\n[Evolution error] {ex.Message}");
                Console.Error.WriteLine($"  Source: {ex.Source}");
                foreach (System.Collections.DictionaryEntry entry in ex.Data)
                    Console.Error.WriteLine($"  Data[{entry.Key}]: {entry.Value}");
                Exception? inner = ex.InnerException;
                int depth = 1;
                while (inner != null)
                {
                    Console.Error.WriteLine($"  Inner[{depth}] ({inner.GetType().Name}): {inner.Message}");
                    inner = inner.InnerException;
                    depth++;
                }
                Console.Error.WriteLine($"  Stack: {ex.StackTrace}");
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
        //  CREATE SALES ORDER — Pastel Evolution SalesOrder SDK
        //
        //  Builds a single-line SalesOrder against one inventory item.
        //
        //  processNow = false  → Save()    order placed, no GL postings,
        //                        returns the generated order number.
        //  processNow = true   → Process() converts to invoice immediately,
        //                        posts to GL/Customer/Inventory ledgers,
        //                        returns the generated invoice number.
        // ================================================================

        private static string CreateSalesOrder(
            string customerCode,
            string itemCode,
            string warehouseCode,
            double quantity,
            double unitPrice,
            bool   processNow,
            string repCode = "")
        {
            Console.WriteLine($"Building order — customer: {customerCode}  item: {itemCode}  warehouse: {warehouseCode}  qty: {quantity}  price: {unitPrice:F2}");

            var SO = new SalesOrder
            {
                Customer    = new Customer(customerCode),
                InvoiceDate = DateTime.Now
            };

            if (!string.IsNullOrWhiteSpace(repCode))
            {
                Console.Write($"  Setting sales rep '{repCode}'... ");
                SO.Representative = new SalesRepresentative(repCode);
                Console.WriteLine("OK");
            }

            Console.Write("  Adding detail line... ");
            OrderDetail od = SO.Detail.Add(itemCode, quantity, unitPrice);
            od.TaxType = new TaxRate(1);
            Console.WriteLine("OK");

            if (processNow)
            {
                Console.Write("  Processing into invoice... ");
                string invoiceNo = SO.Process();
                Console.WriteLine("OK");
                Console.WriteLine($"  Invoice No : {invoiceNo}");
                return invoiceNo;
            }

            Console.Write("  Saving order... ");
            SO.Save();
            string orderNo = SO.OrderNo;
            Console.WriteLine("OK");
            Console.WriteLine($"  Order No   : {orderNo}");
            return orderNo;
        }

        // ================================================================
        //  UPDATE JH INVENTORY SNAPSHOT
        //
        //  Reads current stock levels from the Evolution SQL view
        //  (_bvStockAndWhseItems) for the 7 JH store codes and writes
        //  a full snapshot into the JH web database.
        //
        //  env = "nonprod"  → copy Evolution DB  +  interna1_non_production
        //  env = "prod"     → live Evolution DB  +  interna1_b2b
        // ================================================================

        private static void UpdateJHInventorySnapshot(string env)
        {
            bool isProd  = env.Equals("prod", StringComparison.OrdinalIgnoreCase);
            string jhConn = isProd ? JhConnProd    : JhConnNonProd;
            string evoDb  = isProd ? CompanyDbLive : CompanyDb;

            Console.WriteLine($"[Snapshot] Environment  : {env.ToUpperInvariant()}");
            Console.WriteLine($"[Snapshot] Evolution DB : {evoDb}");
            Console.WriteLine($"[Snapshot] Target DB    : {(isProd ? "interna1_b2b (PROD)" : "interna1_non_production (nonprod)")}");
            Console.WriteLine();

            var totalSw = Stopwatch.StartNew();

            // ---- Phase 1: Load SKU list from JH web DB -------------------------
            Console.Write("[Snapshot] Phase 1 — Loading SKUs from JH web DB... ");
            var sw = Stopwatch.StartNew();
            HashSet<string> skuSet = SnapshotLoadSkuList(jhConn);
            Console.WriteLine($"{skuSet.Count} distinct SKUs  ({sw.ElapsedMilliseconds} ms)");

            // ---- Phase 2: Load inventory per store from Evolution SQL ----------
            Console.WriteLine("[Snapshot] Phase 2 — Loading inventory from Evolution...");
            sw.Restart();
            Dictionary<string, int>           whIdMap = SnapshotLookupWarehouseIds(evoDb);
            Dictionary<string, SnapshotEntry> skuMap  = SnapshotBuildSkuMap(evoDb, whIdMap, skuSet);
            Console.WriteLine($"[Snapshot]   {skuMap.Count} unique SKUs with inventory  ({sw.ElapsedMilliseconds} ms total)");

            // ---- Phase 3: Write text file --------------------------------------
            Console.Write("[Snapshot] Phase 3 — Writing text file... ");
            sw.Restart();
            string dateStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filePath  = Path.Combine(AppContext.BaseDirectory,
                                            $"jh_inventory_snapshot_{env}_{dateStamp}.txt");
            SnapshotWriteTextFile(skuMap, filePath);
            Console.WriteLine($"OK  ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"[Snapshot]   File: {filePath}");

            // ---- Phase 4: Write to JH web DB ----------------------------------
            Console.WriteLine("[Snapshot] Phase 4 — Writing to JH web DB...");
            sw.Restart();
            SnapshotWriteToDatabase(skuMap, jhConn);
            Console.WriteLine($"[Snapshot]   Done  ({sw.ElapsedMilliseconds} ms)");

            Console.WriteLine($"\n[Snapshot] Total time: {totalSw.Elapsed.TotalSeconds:F2}s");
        }

        // ----------------------------------------------------------------
        //  Phase 1 — load all distinct PastelSKUs from the JH web DB
        // ----------------------------------------------------------------

        private static HashSet<string> SnapshotLoadSkuList(string jhConn)
        {
            const string sql =
                "SELECT DISTINCT PastelSKU FROM CurrentOrderSheet_porterandcraftcombined " +
                "WHERE PastelSKU IS NOT NULL AND LEN(PastelSKU) > 0";

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(jhConn);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string sku = reader.GetString(0).Trim();
                if (!string.IsNullOrEmpty(sku)) result.Add(sku);
            }
            return result;
        }

        // ----------------------------------------------------------------
        //  Phase 2a — map JH store codes to Evolution WhseIDs
        //  Evolution's WhseMst stores the 3-char code in cWhseCode.
        // ----------------------------------------------------------------

        private static Dictionary<string, int> SnapshotLookupWarehouseIds(string evoDb)
        {
            string inList  = string.Join(",", JhStoreCodes.Select(c => $"'{c}'"));
            string sql     = $"SELECT WhseLink, Code FROM WhseMst WHERE Code IN ({inList})";
            string connStr = $"Data Source={SqlServer};Initial Catalog={evoDb};" +
                             $"User ID={DbUser};Password={DbPassword};Connection Timeout=30;";

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(connStr);
            conn.Open();
            using var cmd    = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int    id   = reader.GetInt32(0);
                string code = reader.GetString(1).Trim();
                result[code] = id;
                Console.WriteLine($"[Snapshot]   Warehouse {code} → WhseID {id}");
            }

            foreach (string s in JhStoreCodes)
                if (!result.ContainsKey(s))
                    Console.WriteLine($"[Snapshot]   WARNING: store {s} not found in Evolution");

            return result;
        }

        // ----------------------------------------------------------------
        //  Phase 2b — for each store, query _bvStockAndWhseItems and
        //             keep only SKUs that are in the JH SKU list
        // ----------------------------------------------------------------

        private static Dictionary<string, SnapshotEntry> SnapshotBuildSkuMap(
            string evoDb, Dictionary<string, int> whIdMap, HashSet<string> skuSet)
        {
            const string sql =
                "SELECT Code, Description_1, ISNULL(QtyAvailable, 0) AS QtyAvailable " +
                "FROM _bvStockAndWhseItems WHERE WhseID = @WhseID";

            string connStr = $"Data Source={SqlServer};Initial Catalog={evoDb};" +
                             $"User ID={DbUser};Password={DbPassword};Connection Timeout=30;";

            var skuMap = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);

            using var conn = new SqlConnection(connStr);
            conn.Open();

            foreach (string storeCode in JhStoreCodes)
            {
                if (!whIdMap.TryGetValue(storeCode, out int whseId)) continue;

                var sw       = Stopwatch.StartNew();
                int total    = 0;
                int matched  = 0;

                using var cmd    = new SqlCommand(sql, conn) { CommandTimeout = 120 };
                cmd.Parameters.AddWithValue("@WhseID", whseId);
                using var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    total++;
                    string code = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                    if (string.IsNullOrEmpty(code) || !skuSet.Contains(code)) continue;
                    matched++;

                    string desc = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                    int    qty  = Convert.ToInt32(reader.GetValue(2));

                    if (!skuMap.TryGetValue(code, out var entry))
                        skuMap[code] = entry = new SnapshotEntry(desc);

                    entry.StoreQty[storeCode] = qty;
                }

                Console.WriteLine($"[Snapshot]   Store {storeCode} (WhseID {whseId}): " +
                                  $"{total} Evolution rows, {matched} matched  ({sw.ElapsedMilliseconds} ms)");
            }

            return skuMap;
        }

        // ----------------------------------------------------------------
        //  Phase 3 — write the human-readable text file
        // ----------------------------------------------------------------

        private static void SnapshotWriteTextFile(
            Dictionary<string, SnapshotEntry> skuMap, string filePath)
        {
            const int W_CODE = 25;
            const int W_DESC = 50;
            const int W_QTY  = 8;

            var header = new StringBuilder("ItemCode".PadRight(W_CODE) + " " + "Description".PadRight(W_DESC));
            foreach (string s in JhStoreCodes) header.Append(" " + ("Qty" + s).PadLeft(W_QTY));
            header.Append(" " + "Total".PadLeft(W_QTY));
            string divider = new string('─', header.Length);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            writer.WriteLine($"JH Inventory Snapshot  |  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine(divider);
            writer.WriteLine(header);
            writer.WriteLine(divider);

            int rowCount = 0;
            foreach (var kvp in skuMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var line = new StringBuilder(kvp.Key.PadRight(W_CODE) + " " +
                                             kvp.Value.Description.PadRight(W_DESC));
                int total = 0;
                foreach (string s in JhStoreCodes)
                {
                    int qty = kvp.Value.StoreQty.TryGetValue(s, out int q) ? q : 0;
                    line.Append(" " + qty.ToString().PadLeft(W_QTY));
                    total += qty;
                }
                line.Append(" " + total.ToString().PadLeft(W_QTY));
                writer.WriteLine(line);
                rowCount++;
            }

            writer.WriteLine(divider);
            writer.WriteLine($"Total: {rowCount} SKUs  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"[Snapshot]   {rowCount} rows written to text file.");
        }

        // ----------------------------------------------------------------
        //  Phase 4 — write to JHInventory_Snapshot + promote to Golden
        // ----------------------------------------------------------------

        private static void SnapshotWriteToDatabase(
            Dictionary<string, SnapshotEntry> skuMap, string jhConn)
        {
            using var conn = new SqlConnection(jhConn);
            conn.Open();

            SnapshotEnsureSchema(conn, "JHInventory_Snapshot",        "PK_JHInventory_Snapshot",        "CK_JHSnap");
            SnapshotEnsureSchema(conn, "JHInventory_Snapshot_Golden", "PK_JHInventory_Snapshot_Golden", "CK_JHSnapGold");

            DataTable dt = SnapshotBuildDataTable(skuMap);

            using (var tran = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = new SqlCommand("TRUNCATE TABLE JHInventory_Snapshot", conn, tran))
                        cmd.ExecuteNonQuery();

                    using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tran))
                    {
                        bulk.DestinationTableName = "JHInventory_Snapshot";
                        SnapshotAddColumnMappings(bulk);
                        bulk.WriteToServer(dt);
                    }
                    tran.Commit();
                }
                catch { tran.Rollback(); throw; }
            }

            Console.WriteLine($"[Snapshot]   {dt.Rows.Count} rows written to JHInventory_Snapshot.");
            SnapshotPromoteToGolden(conn, dt);
        }

        private static void SnapshotEnsureSchema(
            SqlConnection conn, string table, string pkName, string ckPrefix)
        {
            string createSql = $@"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{table}')
CREATE TABLE [{table}] (
    ItemCode    NVARCHAR(50)  NOT NULL CONSTRAINT [{pkName}] PRIMARY KEY,
    Description NVARCHAR(200) NOT NULL,
    Qty001      INT NOT NULL DEFAULT 0 CONSTRAINT [{ckPrefix}_Qty001] CHECK (Qty001 >= 0),
    Qty004      INT NOT NULL DEFAULT 0 CONSTRAINT [{ckPrefix}_Qty004] CHECK (Qty004 >= 0),
    Qty005      INT NOT NULL DEFAULT 0 CONSTRAINT [{ckPrefix}_Qty005] CHECK (Qty005 >= 0),
    Qty006      INT NOT NULL DEFAULT 0 CONSTRAINT [{ckPrefix}_Qty006] CHECK (Qty006 >= 0),
    Qty007      INT NOT NULL DEFAULT 0 CONSTRAINT [{ckPrefix}_Qty007] CHECK (Qty007 >= 0),
    Qty008      INT NOT NULL DEFAULT 0 CONSTRAINT [{ckPrefix}_Qty008] CHECK (Qty008 >= 0),
    Qty009      INT NOT NULL DEFAULT 0 CONSTRAINT [{ckPrefix}_Qty009] CHECK (Qty009 >= 0),
    QtyTotal    AS (Qty001+Qty004+Qty005+Qty006+Qty007+Qty008+Qty009) PERSISTED,
    LastUpdated DATETIME NOT NULL DEFAULT GETDATE()
)";
            // Migrations for tables created before Qty009 was added
            string addQty009Sql = $@"
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = 'Qty009'
)
    ALTER TABLE [{table}] ADD Qty009 INT NOT NULL DEFAULT 0;";

            string upgradeComputedSql = $@"
IF EXISTS (
    SELECT 1 FROM sys.computed_columns cc
    JOIN sys.objects o ON cc.object_id = o.object_id
    WHERE o.name = '{table}' AND cc.name = 'QtyTotal'
) AND NOT EXISTS (
    SELECT 1 FROM sys.computed_columns cc
    JOIN sys.objects o ON cc.object_id = o.object_id
    WHERE o.name = '{table}' AND cc.name = 'QtyTotal' AND cc.definition LIKE '%Qty009%'
)
BEGIN
    ALTER TABLE [{table}] DROP COLUMN QtyTotal;
    ALTER TABLE [{table}] ADD  QtyTotal AS (Qty001+Qty004+Qty005+Qty006+Qty007+Qty008+Qty009) PERSISTED;
END";

            using (var cmd = new SqlCommand(createSql,        conn)) cmd.ExecuteNonQuery();
            using (var cmd = new SqlCommand(addQty009Sql,     conn)) cmd.ExecuteNonQuery();
            using (var cmd = new SqlCommand(upgradeComputedSql, conn)) cmd.ExecuteNonQuery();
        }

        private static DataTable SnapshotBuildDataTable(Dictionary<string, SnapshotEntry> skuMap)
        {
            var dt = new DataTable();
            dt.Columns.Add("ItemCode",    typeof(string));
            dt.Columns.Add("Description", typeof(string));
            dt.Columns.Add("Qty001",      typeof(int));
            dt.Columns.Add("Qty004",      typeof(int));
            dt.Columns.Add("Qty005",      typeof(int));
            dt.Columns.Add("Qty006",      typeof(int));
            dt.Columns.Add("Qty007",      typeof(int));
            dt.Columns.Add("Qty008",      typeof(int));
            dt.Columns.Add("Qty009",      typeof(int));
            dt.Columns.Add("LastUpdated", typeof(DateTime));

            DateTime now = DateTime.Now;
            foreach (var kvp in skuMap)
            {
                var sq = kvp.Value.StoreQty;
                dt.Rows.Add(
                    kvp.Key,
                    kvp.Value.Description,
                    sq.TryGetValue("001", out int q1) ? q1 : 0,
                    sq.TryGetValue("004", out int q4) ? q4 : 0,
                    sq.TryGetValue("005", out int q5) ? q5 : 0,
                    sq.TryGetValue("006", out int q6) ? q6 : 0,
                    sq.TryGetValue("007", out int q7) ? q7 : 0,
                    sq.TryGetValue("008", out int q8) ? q8 : 0,
                    sq.TryGetValue("009", out int q9) ? q9 : 0,
                    now);
            }
            return dt;
        }

        private static void SnapshotAddColumnMappings(SqlBulkCopy bulk)
        {
            foreach (string col in new[] { "ItemCode", "Description", "Qty001", "Qty004",
                                           "Qty005", "Qty006", "Qty007", "Qty008", "Qty009",
                                           "LastUpdated" })
                bulk.ColumnMappings.Add(col, col);
        }

        private static void SnapshotPromoteToGolden(SqlConnection conn, DataTable dt)
        {
            const string totalsSql = @"
                SELECT
                    (SELECT ISNULL(CAST(SUM(QtyTotal) AS BIGINT), 0) FROM JHInventory_Snapshot)        AS NewTotal,
                    (SELECT ISNULL(CAST(SUM(QtyTotal) AS BIGINT), 0) FROM JHInventory_Snapshot_Golden) AS GoldenTotal";

            long newTotal, goldenTotal;
            using (var cmd = new SqlCommand(totalsSql, conn))
            using (var rdr = cmd.ExecuteReader())
            {
                rdr.Read();
                newTotal    = rdr.GetInt64(0);
                goldenTotal = rdr.GetInt64(1);
            }

            if (goldenTotal > 0)
            {
                double dropPct = (goldenTotal - newTotal) / (double)goldenTotal * 100.0;
                if (dropPct > 10.0)
                {
                    Console.WriteLine($"[Snapshot]   Golden NOT updated — new total ({newTotal:N0}) is " +
                                      $"{dropPct:F1}% below Golden ({goldenTotal:N0}), exceeds 10% threshold.");
                    return;
                }
                long change = newTotal - goldenTotal;
                Console.WriteLine($"[Snapshot]   Promoting to Golden " +
                                  $"(new: {newTotal:N0}, prev: {goldenTotal:N0}, " +
                                  $"change: {(change >= 0 ? "+" : "")}{change:N0}).");
            }
            else
            {
                Console.WriteLine("[Snapshot]   Golden table empty — seeding from snapshot.");
            }

            using (var tran = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = new SqlCommand("TRUNCATE TABLE JHInventory_Snapshot_Golden", conn, tran))
                        cmd.ExecuteNonQuery();

                    using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tran))
                    {
                        bulk.DestinationTableName = "JHInventory_Snapshot_Golden";
                        SnapshotAddColumnMappings(bulk);
                        bulk.WriteToServer(dt);
                    }
                    tran.Commit();
                }
                catch { tran.Rollback(); throw; }
            }

            Console.WriteLine($"[Snapshot]   {dt.Rows.Count} rows written to JHInventory_Snapshot_Golden.");
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

                // Service items have no warehouse stock records — skip them.
                bool isService = !row.IsNull("ServiceItem") && (bool)row["ServiceItem"];
                if (isService) continue;

                string desc = row.IsNull("Description_1") ? string.Empty : row["Description_1"].ToString()!;
                int stockLink = row.IsNull("StockLink") ? -1 : Convert.ToInt32(row["StockLink"]);

                if (processed % 10 == 0)
                    Console.Write($"\r  [SDK] {processed}/{cap}...  ");

                WarehouseContext? wc = null;
                try
                {
                    var item = stockLink >= 0
                        ? new InventoryItem(stockLink)
                        : new InventoryItem(code);
                    wc = item.WarehouseContexts[warehouse.Code];
                }
                catch (EvolutionException ex)
                {
                    errorCount++;
                    if (firstError.Length == 0) firstError = ex.Message;
                    if (showAll) results.Add(new StoreItem(code, desc, 0d, 0d));
                    continue;
                }

                if (wc == null)
                {
                    nullCount++;
                    if (showAll) results.Add(new StoreItem(code, desc, 0d, 0d));
                    continue;
                }

                resolved++;
                double onHand = wc.QtyOnHand;
                double available = wc.QtyFree;
                if (showAll || onHand != 0d || available != 0d)
                    results.Add(new StoreItem(code, desc, onHand, available));
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
                             "QtyAvailable".PadLeft(W_QTY);
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
               item.QtyAvailable.ToString("N2").PadLeft(wQty);

        // ================================================================
        //  HELPERS
        // ================================================================

        private static List<Warehouse> LoadAllWarehouses()
        {
            DataTable dt = Warehouse.List("");
            var result = new List<Warehouse>();
            foreach (DataRow row in dt.Rows)
            {
                int id = Convert.ToInt32(row["WhseLink"]);
                var wh = new Warehouse(id);
                if (wh.Active) result.Add(wh);
            }
            result.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.Ordinal));
            return result;
        }

        private static string PopArg(List<string> args, string flag)
        {
            int i = args.FindIndex(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Count) return string.Empty;
            string val = args[i + 1];
            args.RemoveAt(i + 1);
            args.RemoveAt(i);
            return val;
        }

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

    internal class SnapshotEntry
    {
        public string Description { get; }
        public Dictionary<string, int> StoreQty { get; }

        public SnapshotEntry(string description)
        {
            Description = description;
            StoreQty    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal class StoreItem
    {
        public string Code        { get; }
        public string Description { get; }
        public double QtyOnHand   { get; }
        public double QtyAvailable { get; }

        public StoreItem(string code, string description, double qtyOnHand, double qtyAvailable)
        {
            Code          = code;
            Description   = description;
            QtyOnHand     = qtyOnHand;
            QtyAvailable  = qtyAvailable;
        }
    }
}
