# Verify Program.cs is complete before copying to Windows
# Run this on Mac to check the file

$filePath = "/Users/riaangrobler/FileserverDriveManager/Program.cs"

if (Test-Path $filePath) {
    $lineCount = (Get-Content $filePath).Count
    $lastLine = Get-Content $filePath | Select-Object -Last 1
    
    Write-Host "File: $filePath" -ForegroundColor Cyan
    Write-Host "Total Lines: $lineCount" -ForegroundColor Green
    Write-Host "Last Line: $lastLine" -ForegroundColor Yellow
    
    if ($lineCount -eq 1412 -and $lastLine -eq "}") {
        Write-Host "`n✅ File is COMPLETE and ready to copy!" -ForegroundColor Green
    } else {
        Write-Host "`n❌ File is INCOMPLETE or corrupted!" -ForegroundColor Red
    }
} else {
    Write-Host "File not found: $filePath" -ForegroundColor Red
}
