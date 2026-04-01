# Cleanup script for Veeam Backup (with visible stage progress)
# NOTE: For the console window to appear when started via Task Scheduler,
# set the task to "Run only when user is logged on" and disable "Hidden".

$host.UI.RawUI.WindowTitle = "Cleanup for Veeam"
Write-Progress -Activity "System cleanup" -Status "Starting..." -PercentComplete 0

# 1. Очистка хранилища компонентов (WinSxS) - самое тяжелое
Write-Progress -Activity "System cleanup" -Status "WinSxS / Component Store..." -PercentComplete 10
Write-Host "Cleaning up System Component Store (WinSxS)..." -ForegroundColor Cyan
dism.exe /online /Cleanup-Image /StartComponentCleanup /ResetBase

# 2. Удаление Windows.old и папок обновлений (если они есть)
Write-Progress -Activity "System cleanup" -Status "Removing Windows.old and upgrade leftovers..." -PercentComplete 25
Write-Host "Removing Windows.old and upgrade leftovers..." -ForegroundColor Cyan
$oldDirs = @("C:\Windows.old", "C:\$Windows.~BT", "C:\$Windows.~WS")
foreach ($dir in $oldDirs) {
    if (Test-Path $dir) {
        takeown /F $dir /R /A /D Y
        icacls $dir /grant Administrators:F /T /C /Q
        Remove-Item -Path $dir -Recurse -Force
    }
}

# 3. Очистка кэша Windows Update (SoftwareDistribution)
Write-Progress -Activity "System cleanup" -Status "Cleaning Windows Update Cache (SoftwareDistribution)..." -PercentComplete 40
Write-Host "Cleaning Windows Update Cache..." -ForegroundColor Cyan
net stop wuauserv
net stop bits
Remove-Item -Path "C:\Windows\SoftwareDistribution\*" -Recurse -Force
net start wuauserv
net start bits

# 4. Тотальная чистка временных папок (User и System)
Write-Progress -Activity "System cleanup" -Status "Emptying Temp folders (User/System/Prefetch)..." -PercentComplete 60
Write-Host "Emptying Temp folders..." -ForegroundColor Cyan
$tempPaths = @("$env:TEMP\*", "C:\Windows\Temp\*", "C:\Windows\Prefetch\*")
foreach ($path in $tempPaths) {
    Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. Очистка корзины для всех дисков
Write-Progress -Activity "System cleanup" -Status "Emptying Recycle Bin..." -PercentComplete 75
Write-Host "Emptying Recycle Bin..." -ForegroundColor Cyan
Clear-RecycleBin -Confirm:$false -ErrorAction SilentlyContinue

# 6. Очистка кэша Winget и .NET (NuGet)
Write-Progress -Activity "System cleanup" -Status "Cleaning Dev caches (Winget & NuGet)..." -PercentComplete 90
Write-Host "Cleaning Dev caches (Winget & NuGet)..." -ForegroundColor Cyan
winget cleanup
dotnet nuget locals all --clear

Write-Progress -Activity "System cleanup" -Status "Done" -PercentComplete 100 -Completed
Write-Host "SYSTEM IS CLEAN! Ready for Veeam Backup." -ForegroundColor Green
