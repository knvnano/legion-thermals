# Только чтение: не переключает режимы Lenovo и не записывает таблицы вентиляторов.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $admin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} finally { $identity.Dispose() }
if (!$admin) { throw 'Откройте PowerShell от имени администратора и повторите команду.' }
$output = Join-Path $PSScriptRoot ('results-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output | Out-Null
$report = [ordered]@{ CollectedAt = (Get-Date).ToString('o'); ReadOnly = $true; Errors = @() }
function Read-Method($instance, [string]$method, [hashtable]$arguments = @{}) {
    try {
        $value = Invoke-CimMethod -InputObject $instance -MethodName $method -Arguments $arguments
        $result = [ordered]@{}
        foreach ($p in $value.PSObject.Properties) {
            if ($p.Name -notmatch '^(Cim|PSComputerName)') { $result[$p.Name] = $p.Value }
        }
        return $result
    } catch { return @{ Error = $_.Exception.Message } }
}
try {
    $report.Model = (Get-CimInstance Win32_ComputerSystem).Model
    $report.Bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
    $report.Battery = @(Get-CimInstance Win32_Battery | Select-Object BatteryStatus, EstimatedChargeRemaining)
    $report.Processes = @(Get-CimInstance Win32_Process -Filter "Name='LegionThermals.exe'" | ForEach-Object {
        $version = if ($_.ExecutablePath) { (Get-Item -LiteralPath $_.ExecutablePath).VersionInfo.FileVersion } else { $null }
        [pscustomobject]@{ Pid = $_.ProcessId; Path = $_.ExecutablePath; Version = $version }
    })
    $settings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LegionThermals'
    foreach ($name in @('settings.json', 'fan-session.json')) {
        $path = Join-Path $settings $name
        if (Test-Path -LiteralPath $path) { $report[$name] = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    }
    $game = Get-CimInstance -Namespace root/WMI -ClassName LENOVO_GAMEZONE_DATA
    $fan = Get-CimInstance -Namespace root/WMI -ClassName LENOVO_FAN_METHOD
    $other = Get-CimInstance -Namespace root/WMI -ClassName LENOVO_OTHER_METHOD
    foreach ($method in @('GetSmartFanMode', 'GetThermalMode', 'GetSmartFanSetting', 'GetFan1Speed', 'GetFan2Speed', 'GetFanCoolingStatus')) {
        $report[$method] = Read-Method $game $method
    }
    $report.Features = [ordered]@{}
    foreach ($id in @(0x05040000, 0x05050000, 0x04020000, 0x04030001, 0x04030002)) {
        $report.Features[('0x{0:X8}' -f $id)] = Read-Method $other 'GetFeatureValue' @{ IDs = [uint32]$id }
    }
    $report.Curves = @()
    foreach ($id in @(0, 1, 2)) {
        $report.Curves += @{ FanID = $id; Result = (Read-Method $fan 'Fan_Get_Table' @{ FanID = [byte]$id; SensorID = [byte]0 }) }
    }
    $report.Tables = @(Get-CimInstance -Namespace root/WMI -ClassName LENOVO_FAN_TABLE_DATA |
        Select-Object Mode, Fan_Id, Sensor_ID, FanTable_Len, FanTable_Data, SensorTable_Len, SensorTable_Data, CurrentFanMinSpeed, CurrentFanMaxSpeed)
    $report.Interfaces = @()
    foreach ($class in @('LENOVO_GAMEZONE_DATA', 'LENOVO_FAN_METHOD', 'LENOVO_OTHER_METHOD')) {
        $schema = Get-CimClass -Namespace root/WMI -ClassName $class
        $report.Interfaces += @{ Class = $class; Methods = @($schema.CimClassMethods | ForEach-Object {
            @{ Name = $_.Name; Parameters = @($_.Parameters | Select-Object Name, CimType) }
        }) }
    }
} catch { $report.Errors += $_.ToString() }
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LegionFirmwareRead {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetSystemFirmwareTable(uint provider, uint table, byte[] buffer, uint size);
}
'@
    $id = [BitConverter]::ToUInt32([Text.Encoding]::ASCII.GetBytes('DSDT'), 0)
    $length = [LegionFirmwareRead]::GetSystemFirmwareTable(0x41435049, $id, $null, 0)
    if (!$length) { throw "DSDT: Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
    $bytes = New-Object byte[] $length
    $actual = [LegionFirmwareRead]::GetSystemFirmwareTable(0x41435049, $id, $bytes, $length)
    if ($actual -ne $length) { throw 'DSDT: размер изменился при чтении.' }
    [IO.File]::WriteAllBytes((Join-Path $output 'DSDT.dat'), $bytes)
    $report.DsdtBytes = $length
} catch { $report.Errors += $_.ToString() }
$report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'fan-diagnostics.json') -Encoding UTF8
Write-Host "Результаты сохранены: $output"
Write-Host 'Охлаждение и режим Lenovo не изменялись. Передавать файлы в интернет не требуется.'
