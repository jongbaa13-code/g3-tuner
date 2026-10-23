# เซ็นลายเซ็นดิจิทัลให้ G3Tuner.exe ด้วยใบรับรอง Code Signing ที่ติดตั้งอยู่ในเครื่อง
# (เช่น Certum SimplySign หรือ USB token ของ CA ใดก็ได้ที่ Microsoft รับรอง)
#
#   powershell -ExecutionPolicy Bypass -File .\sign.ps1
#   powershell -ExecutionPolicy Bypass -File .\sign.ps1 -Thumbprint <thumbprint>   (ถ้ามีหลายใบ)
param(
    [string]$File = "G3Tuner.exe",
    [string]$Thumbprint,
    [string]$TimestampServer = "http://time.certum.pl",
    [switch]$AllowSelfSigned   # สำหรับทดสอบเท่านั้น: ใบรับรองแบบนี้ผ่าน Smart App Control ไม่ได้
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$target = (Resolve-Path $File).Path
$rsaOid = "1.2.840.113549.1.1.1"

$certs = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and $_.NotBefore -lt (Get-Date) })
if ($Thumbprint) { $certs = @($certs | Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(" ", "").ToUpper() }) }

if ($certs.Count -eq 0) {
    Write-Host "ไม่พบใบรับรอง Code Signing ที่ใช้งานได้ในเครื่อง" -ForegroundColor Red
    Write-Host "ถ้าใช้ Certum SimplySign ให้เปิดโปรแกรม SimplySign Desktop แล้วล็อกอินก่อน ใบรับรองจึงจะปรากฏ"
    exit 1
}
if ($certs.Count -gt 1) {
    Write-Host "พบใบรับรองหลายใบ ให้ระบุ -Thumbprint:" -ForegroundColor Yellow
    $certs | ForEach-Object { Write-Host ("  {0}  {1}  (หมดอายุ {2:yyyy-MM-dd})" -f $_.Thumbprint, $_.Subject, $_.NotAfter) }
    exit 1
}
$cert = $certs[0]

if ($cert.PublicKey.Oid.Value -ne $rsaOid) {
    Write-Host "ใบรับรองนี้ไม่ใช่แบบ RSA ซึ่ง Smart App Control ไม่รองรับ (รองรับเฉพาะ RSA)" -ForegroundColor Red
    exit 1
}
if ($cert.Subject -eq $cert.Issuer -and -not $AllowSelfSigned) {
    Write-Host "ใบรับรองนี้เป็นแบบ self-signed ซึ่งใช้ผ่าน Smart App Control ไม่ได้" -ForegroundColor Red
    Write-Host "ต้องใช้ใบรับรองที่ออกโดย CA ที่ Microsoft รับรอง"
    exit 1
}

Write-Host "เซ็นด้วย: $($cert.Subject)"
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -Last 1
if ($signtool) {
    & $signtool.FullName sign /fd sha256 /tr $TimestampServer /td sha256 /sha1 $cert.Thumbprint $target
    if ($LASTEXITCODE -ne 0) { Write-Host "signtool เซ็นไม่สำเร็จ" -ForegroundColor Red; exit 1 }
} else {
    $null = Set-AuthenticodeSignature -LiteralPath $target -Certificate $cert -HashAlgorithm SHA256 `
        -TimestampServer $TimestampServer -IncludeChain NotRoot
}

$sig = Get-AuthenticodeSignature -LiteralPath $target
Write-Host ""
Write-Host "สถานะลายเซ็น: $($sig.Status)"
Write-Host "ผู้เซ็น: $($sig.SignerCertificate.Subject)"
if ($sig.TimeStamperCertificate) { Write-Host "ประทับเวลาโดย: $($sig.TimeStamperCertificate.Subject)" }
if ($sig.Status -eq "Valid") {
    Write-Host "เซ็นเรียบร้อย ไฟล์นี้พร้อมใช้งานกับ Smart App Control" -ForegroundColor Green
} else {
    Write-Host "ลายเซ็นยังไม่สมบูรณ์: $($sig.StatusMessage)" -ForegroundColor Yellow
    exit 1
}
