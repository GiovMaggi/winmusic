# Installs VB-CABLE silently on a CI machine (GitHub Actions windows runner).
$ErrorActionPreference = 'Stop'
$zip = "$env:RUNNER_TEMP\vbcable.zip"
$dir = "$env:RUNNER_TEMP\vbcable"
Invoke-WebRequest 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip' -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

# Trust the driver publisher so Windows doesn't show the "Would you like to install this device software?" prompt.
foreach ($cat in Get-ChildItem "$dir\*.cat") {
  $cert = (Get-AuthenticodeSignature $cat.FullName).SignerCertificate
  if ($cert) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'TrustedPublisher', 'LocalMachine'
    $store.Open('ReadWrite'); $store.Add($cert); $store.Close()
  }
}

$p = Start-Process -FilePath "$dir\VBCABLE_Setup_x64.exe" -ArgumentList '-i', '-h' -WorkingDirectory $dir -PassThru
if (-not $p.WaitForExit(180000)) { Stop-Process $p -Force; throw 'VB-CABLE setup timed out' }
Write-Host "VB-CABLE setup exit code: $($p.ExitCode)"

# Give Windows a moment to create the endpoints.
for ($i = 0; $i -lt 30; $i++) {
  $dev = Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue | Where-Object FriendlyName -like '*VB-Audio*'
  if ($dev) { break }
  Start-Sleep 2
}
Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue | Format-Table -AutoSize
Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Format-Table -AutoSize
if (-not $dev) { throw 'VB-CABLE device did not appear' }
Restart-Service Audiosrv -Force
Start-Sleep 3
