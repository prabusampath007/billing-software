<#
.SYNOPSIS
  Checks the printers on this PC for Billing Software and writes a text report to the Desktop.

.DESCRIPTION
  By default this only READS information (printers, drivers, ports, USB devices, print queue, Billing Software install). It prints nothing and changes nothing.
  Send the report file back to support. It holds the computer name, printer names, driver names and Windows version. It holds no bills, customers or passwords.

  Optional tests (each one prints on paper, so only run them when asked and name the printer exactly):
    -PrintWindowsTestPage "Printer name"   Prints the standard Windows test page. If this fails, the problem is the driver, cable or printer, not Billing Software.
    -RawTest "Printer name"                Sends a few plain ESC/POS lines to a RECEIPT printer (not a label printer). Add -Cut to also send the paper-cut
                                           command, and -Drawer to also send the cash-drawer pulse. Shows whether the printer obeys direct commands.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Check-Printers.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Check-Printers.ps1 -PrintWindowsTestPage "TVS RP 3230"

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Check-Printers.ps1 -RawTest "TVS RP 3230" -Cut -Drawer
#>
param(
    [string]$PrintWindowsTestPage = "",
    [string]$RawTest = "",
    [switch]$Cut,
    [switch]$Drawer
)

$ErrorActionPreference = "Continue"
$report = New-Object System.Text.StringBuilder
$findings = New-Object System.Collections.Generic.List[string]

function Add-Line([string]$text) { [void]$report.AppendLine($text) }
function Add-Section([string]$title) { Add-Line ""; Add-Line ("=" * 70); Add-Line $title; Add-Line ("=" * 70) }
function Add-Table($objects) {
    if (-not $objects) { Add-Line "(none)"; return }
    $text = ($objects | Format-Table -AutoSize | Out-String -Width 220).TrimEnd()
    Add-Line $text
}

Add-Line "Billing Software printer check"
Add-Line ("Made: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
Add-Line ("Computer: " + $env:COMPUTERNAME)
try { $os = Get-CimInstance Win32_OperatingSystem; Add-Line ("Windows: " + $os.Caption + " " + $os.Version + " (" + $os.OSArchitecture + ")") } catch { Add-Line "Windows: could not read" }
Add-Line ("PowerShell: " + $PSVersionTable.PSVersion)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Line ("Run as administrator: " + $isAdmin)

# ---------------------------------------------------------------- spooler
Add-Section "1. Print spooler service"
try {
    $spooler = Get-Service -Name Spooler
    Add-Line ("Status: " + $spooler.Status + ", start type: " + $spooler.StartType)
    if ($spooler.Status -ne "Running") { $findings.Add("The Print Spooler service is not running. Start it (services.msc, Print Spooler) or no printer will work.") }
} catch { Add-Line "Could not read the spooler service." }

# ---------------------------------------------------------------- printers
Add-Section "2. Installed printers"
$printers = @()
try { $printers = @(Get-CimInstance Win32_Printer) } catch { Add-Line "Could not read printers: $($_.Exception.Message)" }
$rows = @()
foreach ($p in $printers) {
    $rows += [pscustomobject]@{
        Name = $p.Name; Default = $p.Default; Driver = $p.DriverName; Port = $p.PortName
        Offline = $p.WorkOffline; Shared = $p.Shared; Status = $p.PrinterStatus
    }
}
Add-Table $rows
if ($printers.Count -eq 0) { $findings.Add("Windows lists no printers at all. Install the printer driver and check Settings, Printers & scanners.") }

$tvs = @($printers | Where-Object { $_.Name -match "TVS|RP ?3230|LP ?46|DLITE|label|receipt|thermal|POS|TSC" -or $_.DriverName -match "TVS|TSC|POS|thermal" })
if ($tvs.Count -eq 0) { $findings.Add("No TVS, label, receipt or thermal printer was found in the list above. If the printer is plugged in, its driver is probably not installed.") }
foreach ($p in $printers) {
    if ($p.WorkOffline) { $findings.Add("Printer '$($p.Name)' is set to 'Use printer offline'. Open its queue and untick Printer, Use Printer Offline.") }
    if ($p.DriverName -match "Generic / Text Only") { $findings.Add("Printer '$($p.Name)' uses the 'Generic / Text Only' driver. Use the TVS driver instead, or bars and the rupee sign will not print.") }
    if ($p.PortName -match "^(FILE|nul|PORTPROMPT)") { if ($p.Name -notmatch "PDF|OneNote|XPS|Fax") { $findings.Add("Printer '$($p.Name)' is on port '$($p.PortName)', which is not a real printer port.") } }
}
$default = $printers | Where-Object { $_.Default } | Select-Object -First 1
if ($default -and $default.Name -match "PDF|OneNote|XPS|Fax") { $findings.Add("The Windows default printer is '$($default.Name)'. Billing Software uses the default printer unless a receipt printer is chosen in Settings, Printers.") }

# ---------------------------------------------------------------- ports
Add-Section "3. Printer ports"
try {
    $usedPorts = @($printers | ForEach-Object { $_.PortName } | Sort-Object -Unique)
    $ports = @(Get-CimInstance Win32_TCPIPPrinterPort -ErrorAction SilentlyContinue | Where-Object { $usedPorts -contains $_.Name } | Select-Object Name, HostAddress, PortNumber)
    Add-Line ("Ports in use: " + ($usedPorts -join ", "))
    if ($ports.Count -gt 0) { Add-Line "Network ports:"; Add-Table $ports }
    $usb = @($usedPorts | Where-Object { $_ -match "^USB" })
    if ($usb.Count -gt 0) { Add-Line ("USB ports in use: " + ($usb -join ", ")) } else { Add-Line "No printer is on a USB port." }
} catch { Add-Line "Could not read ports." }

# ---------------------------------------------------------------- drivers
Add-Section "4. Printer drivers"
try {
    $names = @($printers | ForEach-Object { $_.DriverName } | Sort-Object -Unique)
    $drivers = @(Get-CimInstance Win32_PrinterDriver -ErrorAction SilentlyContinue | Where-Object { $n = $_.Name; ($names | Where-Object { $n -like "$_,*" -or $n -eq $_ }).Count -gt 0 } | Select-Object Name, Version, SupportedPlatform)
    if ($drivers.Count -gt 0) { Add-Table $drivers } else { Add-Line ("Driver names: " + ($names -join "; ")) }
} catch { Add-Line "Could not read drivers." }

# ---------------------------------------------------------------- configuration and queue
Add-Section "5. Paper size and queue for each printer"
foreach ($p in $printers) {
    if ($p.Name -match "PDF|OneNote|XPS|Fax") { continue }
    Add-Line ("--- " + $p.Name)
    try { $cfg = Get-PrintConfiguration -PrinterName $p.Name -ErrorAction Stop; Add-Line ("Paper size: " + $cfg.PaperSize + ", color: " + $cfg.Color + ", duplex: " + $cfg.DuplexingMode) } catch { Add-Line "Paper size: could not read (needs Windows 8.1 or later)" }
    try {
        $jobs = @(Get-PrintJob -PrinterName $p.Name -ErrorAction Stop)
        Add-Line ("Waiting jobs: " + $jobs.Count)
        foreach ($j in $jobs) { Add-Line ("  job " + $j.Id + ": " + $j.DocumentName + " (" + $j.JobStatus + ")") }
        if ($jobs.Count -gt 0) { $findings.Add("Printer '$($p.Name)' has $($jobs.Count) job(s) stuck in its queue. Cancel them (Printers & scanners, Open queue, Cancel all) and try again.") }
    } catch { Add-Line "Queue: could not read" }
}

# ---------------------------------------------------------------- USB devices
Add-Section "6. USB and printer devices Windows can see"
try {
    $pnp = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -in @("Printer", "USB", "PrintQueue", "Ports") -or $_.Name -match "print|TVS|TSC|POS|thermal|label|receipt" })
    $pnpRows = $pnp | Select-Object @{n = "Name"; e = { $_.Name } }, @{n = "Class"; e = { $_.PNPClass } }, @{n = "Status"; e = { $_.Status } }, @{n = "ErrorCode"; e = { $_.ConfigManagerErrorCode } }
    Add-Table $pnpRows
    $bad = @($pnp | Where-Object { $_.ConfigManagerErrorCode -ne 0 -and ($_.Name -match "print|TVS|TSC|POS|thermal|label|receipt|USB" ) })
    foreach ($b in $bad) { $findings.Add("Device '$($b.Name)' has a problem (code $($b.ConfigManagerErrorCode)). In Device Manager it shows a warning: reinstall its driver.") }
    $unknown = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -eq 28 })
    foreach ($u in $unknown) { $findings.Add("Device '$($u.Name)' has no driver installed (code 28). If this is the printer, install the TVS driver.") }
} catch { Add-Line "Could not read devices." }

# ---------------------------------------------------------------- Billing Software
Add-Section "7. Billing Software on this PC"
$exe = Join-Path $env:ProgramFiles "Billing Software\BillingSoftware.exe"
if (Test-Path $exe) {
    $v = (Get-Item $exe).VersionInfo
    Add-Line ("Installed: yes, version " + $v.ProductVersion + " at " + (Split-Path $exe))
} else { Add-Line "Installed: not found in Program Files" }
Add-Line ("Data folder exists: " + (Test-Path (Join-Path $env:ProgramData "BillingSoftware")))
Add-Line ("Backup folder exists: " + (Test-Path (Join-Path ([Environment]::GetFolderPath("CommonDocuments")) "BillingSoftware Backups")))
$logDir = Join-Path $env:ProgramData "BillingSoftware\logs"
if (Test-Path $logDir) {
    $log = Get-ChildItem $logDir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        Add-Line ("Newest log: " + $log.Name + " (" + $log.Length + " bytes). Last lines mentioning print problems:")
        $hits = @(Get-Content $log.FullName -Tail 400 -ErrorAction SilentlyContinue | Where-Object { $_ -match "Print|printer" } | Select-Object -Last 8)
        if ($hits.Count -eq 0) { Add-Line "  (none)" } else { foreach ($h in $hits) { Add-Line ("  " + $h.Substring(0, [Math]::Min(300, $h.Length))) } }
    }
}

# ---------------------------------------------------------------- optional tests
if ($PrintWindowsTestPage -ne "") {
    Add-Section "8. Windows test page"
    $target = $printers | Where-Object { $_.Name -eq $PrintWindowsTestPage } | Select-Object -First 1
    if (-not $target) { Add-Line ("No printer is named '" + $PrintWindowsTestPage + "'. Names must match the list in section 2 exactly.") }
    else {
        try { Invoke-CimMethod -InputObject $target -MethodName PrintTestPage | Out-Null; Add-Line ("Test page sent to '" + $target.Name + "'. Did it print? Write yes or no in your message.") }
        catch { Add-Line ("Could not send the test page: " + $_.Exception.Message) }
    }
}

if ($RawTest -ne "") {
    Add-Section "9. Direct command test (ESC/POS)"
    $target = $printers | Where-Object { $_.Name -eq $RawTest } | Select-Object -First 1
    if (-not $target) { Add-Line ("No printer is named '" + $RawTest + "'.") }
    elseif ($target.Name -match "LP ?46|label|DLITE") { Add-Line "Refused: this looks like a LABEL printer. These commands are for receipt printers only." }
    else {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
    }
    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);
    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool StartDocPrinter(IntPtr hPrinter, Int32 level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);
    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, Int32 dwCount, out Int32 dwWritten);

    public static string SendBytes(string printerName, byte[] bytes)
    {
        IntPtr hPrinter;
        if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return "OpenPrinter failed, error " + Marshal.GetLastWin32Error();
        var di = new DOCINFOA { pDocName = "Billing Software direct command test", pDataType = "RAW" };
        string result = "sent";
        if (StartDocPrinter(hPrinter, 1, di))
        {
            if (StartPagePrinter(hPrinter))
            {
                IntPtr p = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                int written;
                if (!WritePrinter(hPrinter, p, bytes.Length, out written)) result = "WritePrinter failed, error " + Marshal.GetLastWin32Error();
                Marshal.FreeCoTaskMem(p);
                EndPagePrinter(hPrinter);
            }
            else result = "StartPagePrinter failed, error " + Marshal.GetLastWin32Error();
            EndDocPrinter(hPrinter);
        }
        else result = "StartDocPrinter failed, error " + Marshal.GetLastWin32Error();
        ClosePrinter(hPrinter);
        return result;
    }
}
"@
        $ascii = [System.Text.Encoding]::ASCII
        $bytes = New-Object System.Collections.Generic.List[byte]
        $bytes.AddRange([byte[]](0x1B, 0x40))                                   # ESC @  initialise
        $bytes.AddRange($ascii.GetBytes("BILLING SOFTWARE`n"))
        $bytes.AddRange($ascii.GetBytes("Direct command test`n"))
        $bytes.AddRange($ascii.GetBytes("123456789012345678901234567890123456789012345678`n"))   # 48 characters: should fit one line on 80 mm paper
        $bytes.AddRange($ascii.GetBytes("`n`n`n"))
        if ($Drawer) { $bytes.AddRange([byte[]](0x1B, 0x70, 0x00, 0x3C, 0x78)) }   # ESC p 0 60 120  cash drawer pulse, pin 2
        if ($Cut) { $bytes.AddRange([byte[]](0x1D, 0x56, 0x42, 0x00)) }            # GS V 66 0  feed and partial cut
        $result = [RawPrinterHelper]::SendBytes($target.Name, $bytes.ToArray())
        Add-Line ("Sent to '" + $target.Name + "': " + $result)
        Add-Line ("Cut command: " + $Cut + ", drawer pulse: " + $Drawer)
        Add-Line "Write down what happened: did text print, did the 48-digit line fit on one line, did it cut, did the drawer open?"
    }
}

# ---------------------------------------------------------------- findings
Add-Section "WHAT THIS CHECK FOUND"
if ($findings.Count -eq 0) { Add-Line "Nothing obviously wrong was found. If printing still fails, send this file with a photo of the printer's own self-test slip." }
else { $n = 1; foreach ($f in $findings) { Add-Line ("$n. $f"); $n++ } }

$desktop = [Environment]::GetFolderPath("Desktop")
$file = Join-Path $desktop ("PrinterReport-" + $env:COMPUTERNAME + "-" + (Get-Date -Format "yyyyMMdd-HHmm") + ".txt")
try { [System.IO.File]::WriteAllText($file, $report.ToString(), (New-Object System.Text.UTF8Encoding($true))); Write-Host "Report saved: $file" -ForegroundColor Green }
catch { $file = Join-Path $env:TEMP (Split-Path $file -Leaf); [System.IO.File]::WriteAllText($file, $report.ToString()); Write-Host "Report saved: $file" -ForegroundColor Green }
Write-Host ""
Write-Host $report.ToString()
