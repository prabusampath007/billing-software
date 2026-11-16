# Billing Software: manual test plan

Run these on the real counter PC (or one just like it) with the real thermal printer and barcode scanner.
Tick each box, write what you saw when it is not as expected, and take a screenshot (Win + Shift + S) of anything that looks wrong.
**Stop and tell me** about anything marked ⛔: those protect money or data.

Installer: `artifacts\installer\BillingSoftware-Setup-1.0.0.exe` (48 MB, not signed, so Windows may show "Windows protected your PC": choose *More info → Run anyway*).
Test files: `docs\test-data\items.csv`, `customers.csv`, `suppliers.csv`.
Where things live: program `C:\Program Files\Billing Software`, data `C:\ProgramData\BillingSoftware`, backups `C:\Users\Public\Documents\BillingSoftware Backups`, logs `C:\ProgramData\BillingSoftware\logs`.

Use fake names and amounts. Tell the tester the Admin password and the recovery key will be needed again later; write them down on paper.

---

## 0. Before you start
- [ ] The thermal printer is installed in Windows and prints a Windows test page.
- [ ] The barcode scanner types into Notepad when you scan something (it acts like a keyboard and presses Enter).
- [ ] Printed copies of a few barcodes are ready (or use the labels you make in section 6).
- [ ] You know the printer's paper width (80 mm or 58 mm).

## 1. Install and first start
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 1.1 | Run the installer, accept the administrator prompt, finish. | No error. A "Billing Software" start-menu entry exists (and a desktop icon if you ticked it). | ☐ |
| 1.2 | Start the program from the start menu. | A **First-time setup** window opens. | ☐ |
| 1.3 | Open `C:\Users\Public\Documents\BillingSoftware Backups`. | The folder exists with `READ ME FIRST.txt` in it. | ☐ |
| 1.4 | Fill in the business (name, state, GSTIN or blank, address, phone), leave **Charge GST on bills** ticked, create the Admin (e.g. `owner`, a password of 8+ characters). | Continues without error. | ☐ |
| 1.5 | The **recovery key** window appears. Write the key on paper (or photograph it) and put it away from this PC. | 64 letters/digits in groups. ⛔ Without it a backup cannot be opened on another PC. | ☐ |
| 1.6 | Sign out, sign in again with the Admin. | The main window opens; the left menu lists Billing, Bills, Items, Customers, Credit, Purchases, Suppliers, Expenses, Day end, GST, Profit & loss, Balance sheet, Opening balances, Import, Reports, Settings. | ☐ |
| 1.7 | Resize the window small and large; maximise; try 125% and 150% Windows display scaling. | Nothing is cut off; the menu collapses to icons when narrow; text is readable. | ☐ |

## 2. Settings and users
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 2.1 | Settings → Users: add a cashier `cashier1`. | Appears in the list. | ☐ |
| 2.2 | Sign out; sign in as the cashier. | The menu has only Billing, Bills and Customers. | ☐ |
| 2.3 | As the cashier, type a wrong password 5 times at sign-in (use a throw-away attempt on the *Admin* name). | The account locks for a while with a clear message ⛔; the right password works after the wait. | ☐ |
| 2.4 | Settings → Billing rules: set auto-lock to 1 minute, wait with the program idle. | The screen locks and asks for the password. Set it back to 10 afterwards. | ☐ |
| 2.5 | Settings → Health check → **Check this installation**. | All lines OK except *Backups* (no backup yet) and possibly *Backup folder is on the Windows disk*. No PROBLEM lines. | ☐ |

## 3. Printers (do this early; most surprises are here)
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 3.1 | Settings → **Printers**: choose the thermal printer as the receipt printer and the right paper width. Save. | "Saved". | ☐ |
| 3.2 | Press **Print a test receipt**, then Print. | A short slip prints. The row of dashes reaches both edges without wrapping or being cut. The ₹ sign prints. Barcode bars are dark and clean. The slip is cut (or stops at the cut line). | ☐ |
| 3.3 | Scan the barcode on the test slip with the scanner into Notepad. | `4006381333931` appears. If it does not scan, note the printer model and darkness setting. | ☐ |
| 3.4 | Does the cash drawer open when a receipt prints? (Only if one is connected.) | Note yes/no. The program does **not** send a drawer command; this depends on the printer driver. | ☐ |
| 3.5 | Choose A4 printer for "document printer" if there is one (or leave blank). | You will be asked each time otherwise. | ☐ |

## 4. Items, import and barcodes
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 4.1 | Import → **Items** → choose `items.csv`. | Preview: **6 ready**, **1 already there/repeated**, **3 with a problem** (bad price, bad barcode, no name). Nothing is saved yet. | ☐ |
| 4.2 | Press **Import 6 rows**. | "Imported 6". Items screen lists Lux Soap, Tata Salt, Basmati Rice (loose), Rice 250 g pack, Notebook, Steel Tumbler. | ☐ |
| 4.3 | Import the same file again. | 0 imported; the six are "already there". | ☐ |
| 4.4 | Items → select *Lux Soap 100g*. | Stock 50 pcs, average cost 28.00, price 45.00, GST 18%. | ☐ |
| 4.5 | Import → **Suppliers** → `suppliers.csv` (date: yesterday). Import → **Customers** → `customers.csv` (date: yesterday). | Suppliers: 2 imported. Customers: 3 imported, 1 problem (bad GSTIN). | ☐ |
| 4.6 | Customers → *Ravi Traders*. | Owes ₹2,500.00, credit limit ₹5,000.00. | ☐ |
| 4.7 | Suppliers → *Metro Wholesale*. | You owe ₹4,000.00. | ☐ |
| 4.8 | Items → *New item*: name "Test Pen", code TP1, price 10, GST 12, unit pcs → **Make a barcode for this item** → Save. | A 13-digit barcode starting `29` is filled in and saved. | ☐ |
| 4.9 | Select Test Pen → **Print labels** (1). Print it on the label printer (or the thermal printer). | A label with name, price, bars and digits. Scan it: the same 13 digits appear. | ☐ |
| 4.10 | Select *Basmati Rice (loose)* → Extra barcodes: code `2900000001077`, quantity `0.25`, note "250 g" → **Add barcode**. | Listed as `× 0.25 kg (250 g)`. | ☐ |
| 4.11 | Items → Rice (loose) → Repack: pack item *Rice 250 g pack*, stock used `10`, packs made `40`. | Preview says each pack costs ₹20.00. Press Repack: Rice (loose) now 15 kg; Rice 250 g pack 40 pcs. | ☐ |

## 5. Billing (use the scanner for some items)
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 5.1 | Billing: scan `2900000001015` (Lux Soap). Scan it again. Change quantity to 2. | Line "Lux Soap 100g" qty 2, total **₹90.00**. | ☐ |
| 5.2 | Pay **cash** ₹100 → Complete (F12). | Change to return ₹10.00. The receipt window opens; **Print** it on the thermal printer. | ☐ |
| 5.3 | Check the printed receipt. | Shop name, GSTIN, "TAX INVOICE", bill no `INV/26-27/000001`, item with HSN and GST 18%, taxable 76.27, CGST + SGST adding to 13.73, TOTAL 90.00, payment, round-off if any. Readable, not cut. | ☐ |
| 5.4 | Scan the **extra barcode** `2900000001077` (a quarter kilo of rice). | Line "Basmati Rice (loose)" quantity **0.25**, ₹30.00. | ☐ |
| 5.5 | Complete a sale paid by **UPI**, and another with **cash + UPI** mixed (cash 50, UPI the rest). | Both complete; payments add up to the total or the button is disabled with a message. | ☐ |
| 5.6 | Pick *Ravi Traders* as customer; sell 5 × Steel Tumbler on **credit** (₹2,250). | Allowed. Ravi now owes ₹4,750. | ☐ |
| 5.7 | Another credit sale to Ravi: 4 × Notebook (₹340). **Sign in as the cashier** for this one. | Admin approval is asked (limit ₹5,000 would be exceeded). Wrong password refuses; the Admin password lets it through. ⛔ | ☐ |
| 5.8 | As cashier, give a ₹10 discount on a line. | Admin approval is asked (discount limit is 0%). | ☐ |
| 5.9 | **Hold bill**: scan two items, press Hold. Scan another item, press **Held bills**, choose the held one. | The screen is empty after holding; recalling swaps the bills; the count shows on the button. | ☐ |
| 5.10 | Bills → pick today's first bill → **View / reprint**. | Receipt marked **DUPLICATE COPY**. | ☐ |
| 5.11 | Bills → **A4 invoice** for a bill. | A4 invoice with amount in words; print to the A4 printer or PDF. | ☐ |
| 5.12 | Sell more of an item than is in stock (e.g. 100 Lux Soap) as cashier. | Admin approval required (negative stock). | ☐ |

## 6. Returns and voids
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 6.1 | Bills → the 2 × Lux Soap bill → **Return items**: return 1, refund cash, reason "damaged", put back in stock. | Credit note `CN/26-27/000001` for ₹45.00; Lux Soap stock goes up by 1. Print the credit note. | ☐ |
| 6.2 | Void a bill that has no returns (reason "test"). | Bill shows voided; stock returns; day totals drop. A cashier needs Admin approval to void. | ☐ |
| 6.3 | Try to void the bill that has a return. | Refused with a clear message. | ☐ |

## 7. Purchases, suppliers, expenses
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 7.1 | Purchases → New: supplier Metro Wholesale, supplier invoice `MW-1`, 20 × Tata Salt at cost 20.00, GST 5%, pay on account. | Saved with number `PUR/26-27/000001`; Tata Salt stock +20; supplier now owed ₹4,000 + the purchase total. | ☐ |
| 7.2 | Enter the same supplier invoice number again. | Refused (already entered). | ☐ |
| 7.3 | Suppliers → pay Metro Wholesale ₹1,000 in cash. | Balance drops by ₹1,000. Paying more than owed is refused. | ☐ |
| 7.4 | Expenses → Rent, ₹11,800, UPI, GST in the amount ₹1,800, supplier GSTIN `33AAAAA1111A1Z5`, bill no `R-1`, tick **Claim the GST as input credit**. | Saved; list shows GST credit 1,800.00. | ☐ |
| 7.5 | Try the same without the supplier GSTIN. | Refused: input credit needs the GSTIN. | ☐ |
| 7.6 | Record "Tea and refreshments" ₹200 cash. | Saved (no credit claimed). Cancel it with a reason. | ☐ |

## 8. Day end
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 8.1 | Day end screen. | "Cash that should be in the drawer" = opening cash you type + cash sales − cash refunds − cash expenses − cash paid to suppliers. Check the arithmetic against your own notes. | ☐ |
| 8.2 | Count the cash (or invent a number) that is ₹10 short; add a note; Close the day. | Closes only with the note; a Z-report prints (80 mm). Next day's opening cash = what you counted. | ☐ |

## 9. Reports
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 9.1 | GST → this month → Prepare. | GSTR-3B style summary with output tax and input credit (purchase + the rent expense); checks list shows any problems. Save to Excel opens in Excel with numbers (not text). | ☐ |
| 9.2 | Profit & loss → this month. | Sales, cost of goods, expenses (rent shown without GST). **Check against your own total of today's sales.** | ☐ |
| 9.3 | Balance sheet → today. | "Assets = liabilities + owner's funds" and the Checks tab has no red items. | ☐ |
| 9.4 | Credit → Who owes you / Statement for Ravi → print statement and payment reminder. | Ravi's balance equals the Customers screen. Statement ends on the same figure. | ☐ |

## 10. Opening balances and period lock
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 10.1 | Opening balances: add Bank ₹50,000 (as at yesterday) and a Loan ₹20,000. | Listed; balance sheet shows the bank and the loan. | ☐ |
| 10.2 | Settings → **Lock earlier periods**: lock up to yesterday (Admin password). Then try to record an expense dated 3 days ago. | Refused: the period is locked ⛔. Today's billing still works. Remove the lock (reason required) afterwards. | ☐ |

## 11. Backup, restore, uninstall, reinstall, reboot ⛔ (the most important section)
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 11.1 | Settings → Backup → **Back up now**. | "Backup completed and verified." A `billing-…db` and `.manifest.json` appear in the backup folder. | ☐ |
| 11.2 | Note the number of bills today (Bills screen). Make 2 more bills. Settings → Restore from a backup → choose the backup. | The preview shows the backup holds fewer bills than now and warns "Restoring will lose the 2 bills". | ☐ |
| 11.3 | Press **Restore this backup…**, confirm, enter the Admin password. | The program restarts. The 2 newer bills are gone; everything else is back. A folder `C:\ProgramData\BillingSoftware\before-restore` holds a safety copy of the data as it was. | ☐ |
| 11.4 | **Restart Windows.** Start the program. | Signs in with the same password; data and backups are all still there. | ☐ |
| 11.5 | Uninstall (Settings → Apps → Billing Software, or `C:\Program Files\Billing Software\unins000.exe`). | A message says the data and backups are kept. | ☐ |
| 11.6 | After uninstalling, look at `C:\ProgramData\BillingSoftware` and `C:\Users\Public\Documents\BillingSoftware Backups`. | **Both are still there** ⛔. `C:\Program Files\Billing Software` is gone. | ☐ |
| 11.7 | Install again, start it. | It goes straight to sign-in (not first-time setup) and **all data is there** ⛔. | ☐ |
| 11.8 | New-PC drill without a second PC: in a Command Prompt run `set BILLINGSOFTWARE_DATA_DIR=C:\Temp\NewPc` then `"C:\Program Files\Billing Software\BillingSoftware.exe"`. On the first-time setup press **I already have a backup…**, choose a file from the backup folder, enter the **recovery key from 1.5**. | The program restarts and signs in with the old Admin password; the old data is there. This proves the recovery key and a backup are enough to recover. Delete `C:\Temp\NewPc` afterwards. | ☐ |
| 11.9 | Copy the backup folder to a USB drive. | Done: this is the off-PC copy the owner needs to keep up. | ☐ |

## 12. GST off, a quick check
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 12.1 | Settings → untick **Charge GST on bills** → Save → confirm. Make a bill. | Receipt says **BILL OF SUPPLY**, number starts `BOS/`, no tax lines. The Items screen hides GST/HSN. The GST screen says GST is off. | ☐ |
| 12.2 | Tick it again. Make a bill. | Back to `INV/…` tax invoices; earlier bills unchanged. | ☐ |

## 13. Support tools
| # | Do this | Expect | ✔ |
|---|---|---|---|
| 13.1 | Settings → **Save a support file…**. Open the zip. | Contains health-check, environment, record-counts and logs; no database, no customer names. | ☐ |

---

## 14. What to send me
For every ☐ that is not ✔: the number (e.g. **5.7**), what you expected, what happened, a screenshot, and (if the program showed an error ID) the ID.
Also send the support file (13.1) and the printer model and Windows version.

## 15. Not covered here (do these separately)
- Updating over an existing install: ask me for a 1.0.1 installer, install it over this one, and check all data is still there.
- Long-running use (leave the program open for a full day) and a large number of bills.
- Accountant review of the GST worksheets, chart of accounts, rounding and numbering.
