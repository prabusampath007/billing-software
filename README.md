# Billing Software (Windows desktop billing and accounting)

Offline WPF desktop application for a small retail shop: one PC, one billing counter, no internet needed.
Built to the frozen decisions in `Decision_Register_v1.1_FROZEN.docx` and `Department_Store_Development_Plan_v1.1.docx`.

**Status: Release 1 foundation + billing, returns, purchases and GST worksheets (Phase 1 and most of Phases 2, 3 and 5, part of Phase 4).
Not yet a complete production release. See "Not built yet" below before delivering to a customer.**

## Layout

```
Codebase/
  BillingSoftware.sln
  global.json                       pins .NET SDK 10
  src/
    BillingSoftware.Domain          money (paise), GST tax, rounding, costing, journal posting rules. No dependencies.
    BillingSoftware.Application     contracts: services, DTOs, exceptions, current user
    BillingSoftware.Infrastructure  EF Core + encrypted SQLite (SQLCipher), services, audit chain, backup
    BillingSoftware.Desktop         WPF app (MVVM): setup, login, billing, items, bills, customers, reports, settings
  tests/BillingSoftware.Tests       328 automated tests (domain rules + end-to-end scenarios on a real encrypted DB)
```

## Build, test, run

Requires the .NET 10 SDK on Windows 10/11.

```bash
dotnet build BillingSoftware.sln
dotnet test tests/BillingSoftware.Tests
dotnet run --project src/BillingSoftware.Desktop
```

Self-contained release build (no .NET needed on the customer PC):

```bash
dotnet publish src/BillingSoftware.Desktop -c Release -r win-x64 --self-contained true -o artifacts/publish/BillingSoftware
```

### Built-in checks (run the .exe with these switches)

| Switch | What it does |
|---|---|
| `--selftest <result.txt>` | Drives the real screen logic end to end (228 checks) on a throw-away encrypted database. Exit code 0 means all passed. |
| `--screenshots <folder>` | Renders every screen at 800x560, 1024x768, 1366x768, 1920x1080 and 2560x1440 to PNG files, using a throw-away database. Used to check responsive layout. |

Both use temporary data and never touch real shop data.

## Where data lives

`C:\ProgramData\BillingSoftware\` (falls back to `%LocalAppData%\BillingSoftware` if not writable). Override with the
`BILLINGSOFTWARE_DATA_DIR` environment variable.

| Item | Path |
|---|---|
| Encrypted database | `billing.db` |
| Database key (Windows DPAPI protected) | `keys\db.key` |
| JSON logs (no customer data) | `logs\` |

The database is encrypted with SQLCipher. The recovery key shown at first run is the only way to open a backup on a new PC.

## What is in

- **Security:** Admin and Cashier roles; Admin approval prompt for discounts above the limit (default 0%), voids, and selling below zero stock;
  lockout after 5 wrong passwords; idle screen lock; tamper-evident (hash-chained) activity log; encrypted database; cashiers never see cost.
- **Billing:** barcode/name search, quantity and discount editing, cash/UPI/card/credit and mixed payments, change calculation,
  per-line GST (inclusive or exclusive, CGST+SGST or IGST, composition = bill of supply), rounding to the rupee with round-off account,
  gapless invoice numbers (`INV/26-27/000001`), duplicate-bill protection, void with stock return and reversal, 80 mm receipt preview and print, DUPLICATE marking on reprints.
- **Stock and accounts:** weighted-average costing, opening stock, loss/gain adjustments, negative-stock handling with cost variance,
  double-entry journals (every posting verified balanced, database CHECK constraints), trial balance, day summary (by payment mode and by cashier), stock valuation.
- **Customers:** credit sales (a named customer is required), outstanding balance, receipts in parts (never more than is owed), credit notes put against the account, and old dues entered under Opening balances. All of it is tied to the books by tests and by the balance sheet's record checks.
- **Sales returns (credit notes):** return part or all of a bill, refund by cash / UPI / card or against the customer's account,
  put the goods back in stock or write them off as damaged, partial returns that add up exactly to the original tax, a bill with
  returns cannot be voided, cashier returns need Admin approval. **An exchange is a return plus a new bill** (two documents).
- **Suppliers and purchases:** supplier master, purchase entry with per-line GST and input-credit (ITC) choice, intra/inter-state
  (CGST+SGST or IGST), cash / UPI / card / on-account payment, the same supplier invoice can never be entered twice, weighted-average
  cost updated, composition dealers never claim credit, payables, paying suppliers in parts, purchase returns (debit notes),
  printable credit notes, debit notes and purchase vouchers.
- **GST worksheets (GST screen, Admin only):** pick a month and get, for the accountant to review (nothing is filed):
  a GSTR-3B style summary (3.1 outward supplies after credit notes, 3.2 inter-state to unregistered customers, 4 input credit after debit
  notes, tax where credit was not claimed, 5 inward 0% supplies, net tax by head); GSTR-1 tables (B2B, B2CL, B2CS, credit notes,
  HSN summary with returns beside sales, document summary with cancelled bills, nil-rated); sales, purchase and credit/debit-note registers;
  and a "checks" list (missing HSN codes, input credit claimed on an unregistered supplier, cancelled bills, large-invoice limit, items the
  software does not record). **Save to Excel** writes one workbook with a sheet per table (money as real numbers); a printable summary is also provided.
  Every figure is tied to the ledger by automated tests (output tax = GST payable accounts, input credit (purchases and expenses) = input-credit accounts, taxable
  value = Sales account). Composition dealers get a turnover figure instead of GSTR-1/3B.
- **Day end (Day end screen, Admin only):** shows the cash that should be in the drawer (opening cash plus cash sales, customer receipts,
  supplier refunds and money put in, less cash refunds, voided bills, purchases, supplier payments and money taken out), the sales of the period
  by payment mode and by cashier, and petty cash in and out (shop expense, owner pay-in / withdrawal, bank deposit / withdrawal). The owner
  counts the cash and closes: the difference is posted to Cash over/short (a note is required when the cash does not match), the first close
  records the opening cash, and the counted cash becomes the next period's opening cash. The closing figures are stored as they were and never
  change (there is no printed day-end report: it was removed on request). **Expected cash always equals the cash account in the books** (tests check this after every kind of movement).
  Closes cover "since the previous close", so more than one close a day is possible.
- **Balance sheet (Balance sheet screen, Admin only):** as at any date (up to a year ahead), next to the same statement for an earlier date
  (one month before unless you choose). Assets (cash, bank/UPI/card, what customers owe, stock, GST credit to claim), liabilities (suppliers,
  unpaid expenses, GST collected) and owner's funds (opening equity, money put in, drawings, profit of earlier years, profit this financial year).
  Profit is computed inside owner's funds and split by financial year (April to March), so no year-end closing step is needed. Tests prove
  assets = liabilities + owner's funds, every line equals its account in the books, and the profit lines equal the profit and loss. The
  **Checks** tab compares the books with the customer accounts, supplier accounts, unpaid expenses and the stock list (today's position), and
  warns about negative cash, bank, stock or dues. Excel file and printable sheet included.
- **Expenses (Expenses screen, Admin only):** record rent, salaries, electricity, water, phone and internet, transport, repairs, packaging,
  advertising, bank charges, interest, insurance, licences, stationery, cleaning, tea, professional fees and "other" (18 starting categories,
  and you can add your own). Each category has its own account, so the profit and loss shows it by name. Choose the date it belongs to
  (back-dating is allowed within an open period, not into the future), who it was paid to, what for, a bill reference, and how it was paid:
  cash from the drawer, UPI, card, or **not paid yet** (rent or salary due), which can then be paid in parts. A mistaken expense can be
  cancelled with a reason (the entry is reversed), but not once payments have been made against it. Filters by dates, category and unpaid,
  a by-category summary, idempotent saving, and everything is Admin-only. **Cash expenses reduce the expected cash at day end**
  (tests keep expected cash equal to the books).
  - **Input GST credit on expenses:** enter the GST included in the bill, the supplier's GSTIN and the bill number, and tick *Claim the GST as input credit*.
    The GST goes to the input-credit accounts (CGST + SGST for a supplier in the shop's state, IGST for another state; an odd paisa goes to SGST) and only the
    amount before GST is the expense, so the profit and loss and the by-category totals show the cost without GST. The whole bill is still paid or owed, and a
    cash expense still takes the whole bill from the drawer. Cancelling the expense takes the credit back. Without a claim the GST stays part of the cost.
    The software refuses a claim without the GST amount, a valid supplier GSTIN and a bill number, on a business on the composition scheme or without a GSTIN,
    for food, drink and refreshments (credit is generally blocked), and for depreciation; and it refuses GST over 28% of the pre-tax amount as a probable typing mistake.
    The GST worksheets add the claimed credit to the 3B input credit (by month of the expense date) and list each such expense in the purchase register
    with the supplier GSTIN and bill number; expenses with GST that was not claimed show under "not claimed", and a warning counts expenses with no credit claimed.
- **Profit and loss (Profit & loss screen, Admin only):** for this month, last month, this or last financial year, today or any dates,
  compared with the period of the same length just before. Sales billed, less bills voided and returns, net sales; other income; less cost of
  goods sold; gross profit and margin; expenses (stock loss, cost differences, round-off, cash over/short, shop expenses paid from the drawer);
  net profit and margin. Also **profit by item** (best first, with loss-making items shown), an Excel file (statement, items, notes) and a
  printable statement. It is read from the posted books, and tests prove net profit equals income less expense, satisfies
  assets = liabilities + capital + profit, and that profit by item adds up to the gross profit exactly. GST is not part of it.
- **Credit customers (Credit screen, Admin only; limit field on the Customers screen):**
  - **Credit limit** per customer (empty = no limit, Admin only). A credit bill that would take what the customer owes (including old dues) over the limit needs the Admin: a cashier gets the Admin approval prompt and nothing is saved if it is refused; the Admin may go over. The billing screen shows "owes X of Y limit - OVER LIMIT" beside the customer.
  - **Who owes you:** every customer with a balance, split into 0-30, 31-60, 61-90 and over 90 days old (payments and credit notes are counted against the oldest amounts first; cancelled bills drop out), most overdue first, customers over their limit in red, with totals and a **Save list to Excel**.
  - **Statement:** pick a customer and dates to see the balance brought forward, every credit bill, receipt, credit note, cancelled bill and opening due with a running balance, ending on exactly what the customer owes. **Print statement** (A4 pages, prints page by page) and **Save statement to Excel**.
  - **Payment reminder:** a printable, polite letter listing the bills still unpaid with their age and the total. Receiving payments stays on the Customers screen.
- **Barcodes and labels (the item's edit dialog on the Items tab) and repacking (Stock tab); Admin only to manage, everyone can scan:**
  - Each item still has its own barcode and code (SKU). It can now also have up to 20 **extra barcodes**, each standing for a quantity (a carton of 12, or 0.25 kg of an item sold by the kilo).
    Scanning one in billing or purchase entry adds that quantity at the item's price, and the same extra barcode scanned again adds another pack to the same line. A barcode can belong to only one item or pack; a 13-digit code whose check digit is wrong is refused as a probable typing mistake (an item saved earlier with such a code can still be edited).
  - **Make a barcode** creates a valid shop-made EAN-13 code (starting 29, a range reserved for in-store use) that never clashes with a manufacturer's code.
  - **Labels:** prints labels (item name, price, EAN-13 bars and digits) for an item or an extra barcode, 1 to 500, with a preview: one label per page for a thermal label printer (size set in Settings, for example 50 x 25 mm), or a sheet of A4 labels three across. Only 13-digit EAN barcodes can be printed.
  - **Repack:** turns bulk stock into another item that has its own barcode, price and GST (25 kg of rice into 100 packs of 250 g). Stock and cost move across at the bulk item's average cost, so there is no profit or loss; whole-paisa rounding goes to cost variance, and packs sold before they existed are costed correctly. It is refused when the bulk stock is not enough or has no cost. There is no cancel; repack back to undo. Preview shows each pack's cost before saving.
- **Opening balances (Opening balances screen, Admin only):** lets the books start from what the business really had: **bank / UPI balance, cash in hand,
  what customers already owe, what you already owe suppliers, loans, and furniture / equipment**. Each entry has an "as at" date (not in the future,
  not into a locked period) and posts one balanced entry against *Opening balance equity* (owner's funds). Customer and supplier dues feed the same balances
  that receipts and supplier payments are checked against, so they can be paid down in parts and cannot be over-paid; the balance sheet's record checks
  include them. Cash recorded here becomes the first day-end close's starting cash (the typed float is then ignored, so it is never counted twice) and cannot be
  added or cancelled after a day-end close. Loans are repaid (or topped up) from the Day end screen ("Loan repaid / received in cash"; repayment is capped at
  the loan still owed). Fixed assets are carried at their value today, and **depreciation is recorded as an expense** in the new Depreciation category with
  "No money involved" (it reduces the asset value and profit, never the cash, and cannot exceed the value left). A wrong entry can be cancelled with a reason
  (reversed, not deleted) unless it has since been used (a due paid against, a loan repaid, an asset depreciated, cash counted). Opening *stock* stays on the
  Items screen and is shown here as a memo. Everything is idempotent and audited. "No money involved" is refused everywhere a real payment is taken or made.
- **Place of supply** is now stored on each bill (from the customer's state or GSTIN). Older bills fall back to the customer record or the
  business state. A customer's state must agree with their GSTIN.
- **Safe upgrade:** a database made by an earlier release is upgraded automatically on start (new tables, plus the one numbered column step; proven by tests). Schema version is now 12.
- **Default backup folder:** on a standard installation with no folder chosen, the program picks one at first start and shows it in Settings: **`C:\Users\Public\Documents\BillingSoftware Backups`** on the Windows (C:) drive is the first choice (shared Documents: easy to find, writable by every user, outside the program's own data folder, and **not removed when the software is uninstalled, updated or Windows restarts**); only if that cannot be created or written to does it fall back to another fixed hard disk with 2 GB or more free (`D:\BillingSoftware Backups`; removable USB disks are never used because they may be unplugged). The installer script creates the folder and leaves it in place on uninstall. A `READ ME FIRST.txt` is left in the folder explaining the files, the recovery key and how to restore. Settings has **Open the backup folder** for copying it to a USB drive. A folder the owner chose is never replaced. When the folder is on the Windows disk the warning banner still says so.
- **Backup:** online backup (safe while running), integrity check, checksum and manifest, retention (7 daily / 4 weekly / 12 monthly), warning banner if overdue or on the system disk, every 2 hours while open.
- **GST can be turned off (Settings, "Charge GST on bills"; on by default, also asked at first setup):** for a business that does not charge GST. While it is off, new bills are bills of supply (series BOS) with no tax at the item's price, a customer in another state is not charged IGST, GST paid on purchases becomes part of the stock cost and no input credit is claimed (also refused on expenses), the Items screen hides the GST rate, HSN code and "price includes GST", and the GST worksheets are not available (the screen says GST is off). Turning it off also ends the composition setting. Nothing already recorded changes: earlier tax invoices keep their tax and can still be returned with it, and each series (INV, BOS) stays gapless. Turning it back on resumes tax invoices. Changing it asks for confirmation and is logged. The GST item stays in the menu; its screen explains that GST is off.
- **Item code optional, Tamil names:** the item code can be left empty (one is made: `ITEM-00001`, `ITEM-00002` ..., never clashing with a typed code or a barcode; an existing item keeps its code). Each item can have a **Tamil name** in its own field (Windows' Tamil font, Nirmala UI); **receipts print the Tamil name instead of the English one when it is filled in** (the name as it was on the day is kept with each bill line), and items can be found by typing the Tamil name. The import has a "Tamil name" column. Typing Tamil needs the Tamil keyboard to be added in Windows. A4 invoices and credit notes still show the English name. Printing is as a picture through the printer driver, so the font only has to exist on the PC.
- **Everyday screen fixes:** results appear **while typing** in the billing search box (matching items with code and price; Enter or a click adds one; the scanner still works as before); scroll bars are thin and the same everywhere, the mouse wheel scrolls the page even when the pointer is over a table, list or text box (it hands over at the end of the inner one) and scrolls gently (one row a notch in tables); Settings and the item panel have a **Close** button (closing the item panel gives the list the whole width); long text in table cells ends in "..." and shows in full on hover; the "backups are on the same disk" warning is gone (the banner now appears only when no backup folder is set or the last backup is over a day old); the Import screen shows the file's own columns as a table, closes its right-hand panel when the import is done and "Import another file" clears everything back to normal.
- **New look and layout (1.1):** a light-blue header with the shop name and who is signed in (Lock and Sign out on the right), an **icon rail** on the left with the sections (Sell, Items, People, Money, Accounts, Settings) and **tabs along the top** for the pages in each section (Sell: Billing, Bills. Items: Items, Stock, Import. People: Customers, Credit, Suppliers. Money: Purchases, Expenses, Day end. Accounts: GST, Profit & loss, Balance sheet, Opening balances, Reports). The cashier sees Sell and People (Customers) only. Ctrl+1 to Ctrl+9 open the first nine pages in that order; Ctrl+L locks. Tables have light-blue headings and alternate row shading.
- **Items and Stock are separate tabs:** the **Items** tab is a full-width table (item, Tamil name, code, HSN, price, GST, on hand, status; the search follows your typing). **+ New item** or choosing a row opens the **Edit item dialog** with cards for Item details, Price and tax, Barcodes (with extra barcodes) and Barcode labels; Save item or Cancel (Esc) closes it, and on a small window it becomes one column and scrolls. The **Stock** tab shows on hand, average cost and value for every item (with the total stock worth at cost and how many are out of stock, and an "Out of stock only" filter) and a side panel to add stock, adjust for loss or damage, and repack.
- **Restore from a backup (Settings):** lists the backups in the backup folder (or any file you choose), checks the file (not damaged, checksum matches, the books balance, not from a newer version, opens with this computer's key) and shows what it holds next to what you have now (bills, last bill number, bills that would be lost). Restoring needs the Admin password and a second confirmation; the backup is staged, and the program restarts and carries the restore out **before** it opens the database. Today's data is always kept first (a safety copy in `before-restore`, with the old key if the key changes), a restore that cannot be completed leaves the data as it was, and the restore is written to the activity log. **First run on a new computer:** the setup window has "I already have a backup..." that takes the backup file and its recovery key.
- **Period lock (Settings):** lock everything up to a date (it must be before today) once the accountant has the figures; nothing can then be dated into it and bills, expenses and balances from it cannot be cancelled. Moving the lock back or removing it needs a reason; every change needs the Admin password and is logged.
- **Hold and recall bills (Billing):** put the bill aside while the customer fetches something and bring it back (also after a restart, by anyone at the counter; up to 30 held bills; kept for 3 days; prices are taken fresh when it comes back, and items no longer for sale are left out and reported). A held bill is not a sale: no stock moves and nothing is posted.
- **Data import (Import screen, Admin only):** items (with opening stock and cost), customers (credit limit and opening due) and suppliers (opening payable) from an Excel or CSV file, with a blank template. The preview checks every row by the same rules as typing it in and shows what will happen to each; rows already there (same code, barcode, name and phone, or GSTIN) are skipped, so the file can be imported again safely; rows with a problem are listed and not imported. Opening dues are posted through Opening balances with the date you choose.
- **A4 tax invoice (Bills screen, "A4 invoice"):** the full invoice on A4 pages with the business, buyer, items with HSN, GST by rate, totals, amount in words and the signature line; the copy is marked DUPLICATE like the receipt.
- **Printers (Settings):** the receipt printer and paper width (**80 mm or 58 mm thermal roll**), a printer for A4 documents (statements, reports, invoices), and a label printer with the label size (for example 50 x 25 mm, one label per page) or A4 label sheets. A4 documents and labels ask which printer to use when none is chosen, so they are never squeezed onto the receipt roll. **Print a test receipt** shows the paper width, text sizes and a barcode.
- **Health check and support file (Settings):** checks the database, the books, the activity log, backups, disk space and the data folder; **Save a support file** writes one zip (health check, versions, record counts, recent logs) with no bills, customer details, passwords, database or keys.
- **Installer:** `installer\BillingSoftware.iss` and `installer\build-installer.ps1` (publish, self-test, Inno Setup build, optional signing). **Not yet compiled or run** (see "Not built yet").
- **Layout:** responsive from about 420 px wide up to 2560x1440; navigation collapses to icons below 1000 px; PerMonitorV2 DPI aware.

## Not built yet (must be done or consciously accepted before a customer go-live)

- Bill-level discount; price override in the UI; a single "exchange" screen (today: return, then a new bill).
- Day end has no cash denomination counter, and cashiers cannot record petty cash or close (Admin only).
- Opening balances: depreciation is entered by hand (no automatic yearly or monthly schedule, no depreciation-rate calculation, no sale or disposal of an asset). Each loan is a single running balance (no instalment schedule or interest posting). Opening stock is entered per item on the Items screen, not here. Supplier payments are not yet in the day summary.
- **Barcodes:** labels print only for 13-digit EAN codes (one per label on a label printer, or A4 sheets; no Code 128 or QR). Label printing and scanning have not been tested with a real printer or scanner; check a printed label with the customer's scanner before relabelling stock. An extra barcode's price is the item's price for that quantity (a pack with its own price should be a separate item made with Repack). Repacking has no packing-material cost or wastage entry (record wastage as a stock loss) and no cancel.
- Profit and loss shows only what is in the books: interest accruals, income tax and other year-end adjustments must be entered as expenses or added by the accountant (depreciation is entered as an expense; see Opening balances). Recurring expenses are not automatic, and expense receipts/attachments are not stored.
- GST: the worksheets are not the portal's upload formats (no GSTN JSON), and nothing is filed. Not recorded at all: reverse-charge purchases,
  exports, e-commerce supplies, input-credit reversals, e-invoicing / e-way bill, GSTR-2B reconciliation. The large-invoice (B2CL) limit is a
  setting that must be confirmed with the accountant; the composition return (CMP-08 / GSTR-4) is only a turnover figure.
- GST on expenses: reverse-charge (for example rent from an unregistered owner or goods transport) is not handled; the unclaimed GST of an expense is not split by head in 3B "tax where credit was not claimed" (it shows in the purchase register); one expense carries one GST amount (no per-rate split); credit is claimed in the month of the expense date, with no time-limit or matching against GSTR-2B. The blocked-credit rule covers refreshments only; the accountant decides other cases (vehicles, personal use, gifts).
- Multi-unit conversion (for example buying by the carton and selling by the piece, other than repacking). Import covers items, customers and suppliers only: no opening bills or sales history, no purchase history, and only the first sheet of an Excel file is read.
- Credit customers: reminders are printed only (nothing is sent by WhatsApp, SMS or email); no credit period per customer or due dates on bills (age is counted from the bill date); no interest or late fees; no batch printing of statements; the ageing is as at today only.
- **Installer: built and installed once, on the development PC only.** `installer/build-installer.ps1` published, passed the self-test and compiled `BillingSoftware-Setup-1.0.0.exe` (48 MB, unsigned) with Inno Setup 6.7.3; a silent install put the program in `C:\Program Files\Billing Software`, created the data and backup folders with the right permissions and a start-menu shortcut, registered an uninstaller, and the installed program started on "First-time setup". **Not yet tested:** a clean second PC, updating over an existing install, uninstalling (the data and backup folders are meant to stay) and reinstalling, and restarting Windows: `docs\Manual_Test_Plan.md` covers these. **Code signing** needs a certificate (the script signs when given one); an unsigned installer shows a Windows warning. No Windows Event Log entries.
- **Nothing has been printed on a real printer.** Receipts print through the Windows printer driver on one continuous page cut to the content (the customer uses a thermal printer): paper width, margins, the rupee sign, the barcode, the cut and whether the cash drawer opens depend on the printer and driver. Direct ESC/POS commands (cash-drawer kick, partial cut, 58 mm tuning) are not sent; a driver that does not cut at the end of a job will need its own setting. Use "Print a test receipt", the label and A4 prints and a barcode scan on the customer's hardware first.
- The chart of accounts, rounding rule, invoice number format and GST treatment are **not yet signed off by an accountant**.
- Backup destination (D4): the program now chooses one itself (see "Default backup folder"), so backups work from day one. The customer can still change it in Settings. The files should be copied off the PC regularly: a backup that stays on the same computer does not protect against the computer being lost, stolen or its disk failing.

## Testing summary

- 328 automated tests: tax and rounding, posting balance, costing, security, approvals, idempotency, voids, credit, backups, a full restore drill
  using the recovery key, sales returns, purchases, supplier payables, purchase returns, the GST worksheets (tied to the ledger, including the
  Excel file's contents), the day-end close (expected cash equal to the books, variances, stored closing figures), credit limits, statements, ageing and reminders (every figure ties to the customers' balances and the books), opening balances (customer and supplier dues against the books, loans, fixed assets and depreciation, first-close cash, back-dating and the period lock) restoring a backup (checked, staged, applied at the next start, old data kept, a backup from another computer with its recovery key), the period lock, held bills, imports, the health check and support file, amounts in words, and the upgrades from earlier releases' databases.
- 228 end-to-end screen-logic checks (`--selftest`: billing, return dialog, purchase entry, suppliers, purchases, GST screen and export, Expenses, Day end screen, Profit & loss screen, Balance sheet screen, Opening balances screen, the Items table and edit dialog, the Stock tab, barcodes, labels and repacking, Credit screen, hold and recall, period lock, restore, printers, health check, import, A4 invoice), also run against the published self-contained build.
- Layout rendered and inspected at 800, 1024, 1366, 1920 and 2560 px wide (plus 420 px for the setup window).
- Not covered: real printers and scanners, displays scaled above 100%, touch screens, screen readers beyond basic automation names, long-running soak tests, load tests at the target volumes.
