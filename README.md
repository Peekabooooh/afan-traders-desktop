# AFAN Traders – Desktop App

Native desktop app for quotations → delivery challans → invoices, with inventory and purchases.

| Platform | Tech | Folder | Status |
|---|---|---|---|
| macOS 14+ | Swift · SwiftUI · AppKit · PDFKit · SQLite | `macOS/` | ✅ v1 |
| Windows 10/11 | C# · WPF (Windows 11 Fluent style) · SQLite · Windows PDF engine | `windows/` | ✅ v1 |

Both apps use the **same SQLite database layout** (`macOS/Sources/AfanTraders/Data/Schema.swift`),
so a backup made on one can be opened by the other.

## macOS

```sh
cd macOS
./scripts/build-app.sh          # → dist/AFAN Traders.app (Apple Silicon + Intel)
open "dist/AFAN Traders.app"
```

Open `macOS/Package.swift` in Xcode to edit and run it from there.

- Data is stored in `~/Library/Application Support/AFAN Traders/afan-traders.sqlite`
  (Settings → Data & Backup → *Save a Backup Copy…*).
- `--data <folder>` opens a different database (for training/testing).
- `--selftest <folder>` runs the whole workflow on a scratch database and writes sample PDFs.

## Windows

```sh
cd windows
./build-installer.sh          # → windows/dist/AFAN-Traders-Setup-1.0.0.exe (can be built on a Mac)
```

- **Install:** run `AFAN-Traders-Setup-1.0.0.exe` on the PC. It installs to Program Files and adds Start Menu and
  Desktop shortcuts, plus a firewall rule for office sync on private networks only. .NET is bundled, so nothing else is needed.
  Requires Windows 10 (version 2004) or Windows 11, 64-bit.
- **Data:** `%LOCALAPPDATA%\AFAN Traders\afan-traders.sqlite`, the same format as the Mac app.
  Uninstalling keeps the data.
- **Same features as the Mac app:** quotations, delivery challans, invoices, bulk stock-in, inventory with pictures
  and stock history, reports with Excel export, letterhead printing, office network sync and cloud sync.
- **Works with the Mac app:** Mac and Windows computers sync over the office network (same office code), and a
  database or backup can move between them.
- `windows/AfanTraders.Core` holds the shared logic (data, rules, reports, PDFs, sync) and is tested on any OS:
  `dotnet run --project AfanTraders.Tests -- selftest|synctest|lantest <folder>`.
- **Screen test on a Windows PC:** `"AFAN Traders.exe" --uitest C:\temp\uitest` opens every page and window and
  writes screenshots plus `uitest.log`.
- `.github/workflows/windows.yml` runs all of this on a GitHub Windows machine.

## How it works

1. **Purchases** add stock. Type an Item ID: existing items fill in their saved details;
   unknown IDs become new items.
2. **Quotation**: pick items from inventory, set qty and rate. Sales tax starts at the default
   (18%, set in Settings) and can be changed on each quotation.
3. **Delivery Challan**: made from a quotation. Same items, **no prices**, for the fleet/gate.
   **Approving** it removes those quantities from stock.
4. **Invoice**: made from an approved challan. Starts at the quotation prices; change any rate,
   or apply a margin % to all of them.
5. The **PO number** links all three. The Quotations screen shows every order's challan and invoice status.

Quotation, DC and invoice numbers are random (`QT-26-48213`) and can be edited. Cost prices are hidden
every time the app starts. The eye button (⇧⌘K) shows them.
Upload your letterhead PDF in Settings and all three documents print on it.

## Reports

Sidebar → **Reports**. Pick a period (Today, This Week, This Month, Last Month, This Year, All Time or Custom), then:

| Report | Shows |
|---|---|
| Overview | Net sales, sales tax, cost of goods, gross profit and margin, purchases, open quotations, stock value, a sales/profit chart, top items and top customers |
| Day by Day | Each day's bills, qty, sales, tax, cost, profit, margin and purchases |
| Bill by Bill | Every sales invoice with its cost, profit and margin |
| Line Detail | Every item on every bill: sale rate, quoted rate, unit cost, profit |
| Item by Item | Qty sold, sales, average rate, cost, profit and margin per item, plus purchased qty and stock |
| Customers | Sales, profit and margin per customer, last bill, open quotations |
| Orders | Each quotation's status, its invoice, and any price change from quote to invoice |
| Purchases | Every purchase invoice (stock in) |
| Stock Value | Stock at cost and at sale price, potential profit, stock in/out in the period |

Click a column header to sort, type in the filter box to narrow the rows, and double-click a row to open its document.
**Export to Excel** saves a CSV and **Print / PDF** prints the table. Cost and profit are masked, and left out of
exports and printouts, while cost prices are hidden.
Profit uses each item's cost **at the time it was invoiced**, so later cost changes don't alter old figures.

## Office network sync – free, no internet

Settings → **Office Network Sync**: turn it on on every computer with the **same office code**.
Computers on the same Wi-Fi or office network find each other automatically (Bonjour) and exchange changes
directly, within about a second, with no internet and no monthly cost.
- Each computer asks the others "what changed since #N?" (a local change counter, `lan_seq`) and applies the
  answer with the same newer-edit-wins rules as cloud sync. A computer passes on the changes it receives, so A → B → C works.
- The office code is checked with HMAC challenges in both directions and is never sent over the network.
  A computer with a different code receives nothing. The code is stored in the Keychain.
- It works alongside Cloud Sync: changes received over the office network are also queued for the cloud.
- `--lantest <folder>` runs a multi-computer test over real TCP connections.

## Cloud sync (Supabase) – offline first

Optional. The **free plan is enough**, so there's no need for the $25 plan. Item pictures are saved as small JPEGs to stay within it.


- **This Mac's database is always the working copy.** The app works without internet.
- Every change is marked "waiting". When online, the app downloads newer changes from Supabase and then uploads its own.
  Changes from other computers come in instantly over a realtime connection; there is also a check every 2 minutes.
- **Conflicts:** the newer edit of a document wins. The Supabase trigger enforces this too, so an old offline edit
  can't overwrite a newer one. If two computers create the same Item ID or document number offline, both records
  are kept and the second one gets "-2".
- **Stock** is calculated as manual adjustment + purchased − delivered (approved challans). Stock added on two
  computers at the same time is always counted correctly.
- **Setup:** Settings → Cloud Sync → *Copy Setup SQL*, then run it in Supabase SQL Editor. Add a user under
  Authentication → Users, then enter the Project URL, anon key, email and password in the app.
  The sign-in token is kept in the macOS Keychain.
- Cloud tables: `afan_items`, `afan_purchases`, `afan_quotations`, `afan_challans`, `afan_invoices`, `afan_settings`
  (`uid`, `data` JSON, `updated_at`, `deleted`, `device`, `synced_at`). The Windows app will use the same tables.
- `--synctest <folder>` runs a two-computer sync simulation.

## Storage

Everything is stored locally in one SQLite file:
`~/Library/Application Support/AFAN Traders/afan-traders.sqlite`.
No internet and no cloud. Item pictures, the logo and the letterhead are stored inside the same file.
Use Settings → Data & Backup to save a copy.

## Icons

3D icons are in `macOS/Resources/Icons/`: Microsoft Fluent 3D, MIT license (see `LICENSE-FluentEmoji.txt`).
To use another set, such as IconScout, replace the PNGs and keep the same file names. No code change is needed.
