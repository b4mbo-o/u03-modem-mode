#requires -Version 5.1
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
    [switch]$ToModem,
    [switch]$ToRndis,
    [switch]$Status,
    [switch]$Yes,
    [string]$InterfaceAlias,
    [string]$DeviceIp = "192.168.100.1",
    [string]$HostIp = "192.168.100.3",
    [switch]$Version
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$ToolVersion = "0.4.0-dev"
$VendorId = "19D2"
$ProductStorage = "1484"
$ProductRndis = "1483"
$ProductModem = "1481"
$ProductDiag = "0016"
$AuthSalt = "94CB8A5309AF41BDBFC855A9BF2A3A5E"
$DeviceHost = "speedusb-stick.home"
$Referer = "http://speedusb-stick.home/index.html"
$script:TemporaryIpAdded = $false
$script:TemporaryInterfaceIndex = 0
$script:SourceIp = $null
$script:CurlPath = $null

function Write-Log([string]$Message) {
    Write-Host "[u03] $Message"
}

function Write-WarningLog([string]$Message) {
    Write-Warning "[u03] $Message"
}

function Get-ModeName([string]$ProductId) {
    switch ($ProductId) {
        $ProductStorage { return "virtual CD-ROM" }
        $ProductRndis { return "RNDIS/Web UI" }
        $ProductModem { return "modem" }
        $ProductDiag { return "factory/diagnostic" }
        default { return "unknown" }
    }
}

function Get-PresentPnpEntities {
    if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
        return @(Get-PnpDevice -PresentOnly | ForEach-Object {
            [pscustomobject]@{
                InstanceId = $_.InstanceId
                Name = $_.FriendlyName
            }
        })
    }

    return @(Get-CimInstance -ClassName Win32_PnPEntity | Where-Object {
        -not $_.PSObject.Properties["Present"] -or $_.Present
    } | ForEach-Object {
        [pscustomobject]@{
            InstanceId = $_.PNPDeviceID
            Name = $_.Name
        }
    })
}

function Get-U03State([switch]$Quiet) {
    $entities = @(Get-PresentPnpEntities | Where-Object {
        $_.InstanceId -match "VID_$VendorId&PID_(1484|1483|1481|0016)"
    })

    $productIds = @($entities | ForEach-Object {
        if ($_.InstanceId -match "VID_$VendorId&PID_(1484|1483|1481|0016)") {
            $Matches[1].ToUpperInvariant()
        }
    } | Sort-Object -Unique)

    if ($productIds.Count -eq 0) {
        if ($Quiet) { return $null }
        throw "no supported ZTE U03 USB ID found"
    }
    if ($productIds.Count -ne 1) {
        throw "multiple U03 USB states are present: $($productIds -join ', ')"
    }

    return [pscustomobject]@{
        ProductId = $productIds[0]
        Mode = Get-ModeName $productIds[0]
        Entities = $entities
    }
}

function Get-U03ComPorts([string]$ProductId) {
    $ports = @(Get-PresentPnpEntities | Where-Object {
        $_.InstanceId -match "VID_$VendorId&PID_$ProductId" -and
        $_.Name -match "\(COM[0-9]+\)"
    } | ForEach-Object {
        if ($_.Name -match "\((COM[0-9]+)\)") {
            [pscustomobject]@{
                Port = $Matches[1]
                InstanceId = $_.InstanceId
                Name = $_.Name
            }
        }
    })
    return $ports
}

function Show-U03Status {
    $state = Get-U03State
    Write-Output "USB ID: 19d2:$($state.ProductId.ToLowerInvariant())"
    Write-Output "Mode:   $($state.Mode)"
    if ($state.ProductId -eq $ProductModem -or $state.ProductId -eq $ProductDiag) {
        $ports = @(Get-U03ComPorts $state.ProductId)
        if ($ports.Count -gt 0) {
            Write-Output "Ports:"
            $ports | ForEach-Object { Write-Output "  $($_.Port)  $($_.Name)" }
        } else {
            Write-Output "Ports:  not available (install the ZTE serial/modem driver)"
        }
    }
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Confirm-U03Change([string]$ExpectedWord, [string]$Message) {
    if ($Yes) { return }
    Write-Host ""
    Write-Host $Message
    $answer = Read-Host "Type $ExpectedWord to continue"
    if ($answer -cne $ExpectedWord) {
        throw "cancelled"
    }
}

function Wait-U03Product([string[]]$Expected, [int]$TimeoutSeconds = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $state = Get-U03State -Quiet
            if ($null -ne $state -and $Expected -contains $state.ProductId) {
                return $state
            }
        } catch {
            # Windows can briefly show both old and new interfaces during re-enumeration.
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Get-U03RndisAdapter {
    if ($InterfaceAlias) {
        return Get-NetAdapter -Name $InterfaceAlias
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $candidates = @(Get-CimInstance -ClassName Win32_NetworkAdapter | Where-Object {
            $_.PNPDeviceID -match "VID_$VendorId&PID_$ProductRndis"
        })
        if ($candidates.Count -eq 1) {
            return Get-NetAdapter -InterfaceIndex $candidates[0].InterfaceIndex
        }
        if ($candidates.Count -gt 1) {
            throw "multiple U03 RNDIS adapters found; specify -InterfaceAlias"
        }
        Start-Sleep -Milliseconds 500
    }
    throw "could not find the U03 RNDIS network adapter"
}

function Initialize-U03Network {
    $adapter = Get-U03RndisAdapter
    Write-Log "using RNDIS adapter: $($adapter.Name)"

    if ($adapter.Status -eq "Disabled") {
        throw "the U03 RNDIS adapter is disabled: $($adapter.Name)"
    }

    $addresses = @(Get-NetIPAddress -InterfaceIndex $adapter.InterfaceIndex `
        -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
            $_.IPAddress -ne "0.0.0.0" -and $_.IPAddress -notlike "169.254.*"
        })
    $deviceSubnetAddress = @($addresses | Where-Object {
        $_.IPAddress -like "192.168.100.*" -and $_.IPAddress -ne $DeviceIp
    } | Select-Object -First 1)

    if ($deviceSubnetAddress.Count -gt 0) {
        $script:SourceIp = $deviceSubnetAddress[0].IPAddress
        return
    }

    Write-Log "temporarily adding $HostIp/24 to $($adapter.Name)"
    New-NetIPAddress -InterfaceIndex $adapter.InterfaceIndex -IPAddress $HostIp `
        -PrefixLength 24 -AddressFamily IPv4 | Out-Null
    $script:TemporaryIpAdded = $true
    $script:TemporaryInterfaceIndex = $adapter.InterfaceIndex
    $script:SourceIp = $HostIp
}

function Remove-U03TemporaryNetwork {
    if ($script:TemporaryIpAdded) {
        try {
            Remove-NetIPAddress -InterfaceIndex $script:TemporaryInterfaceIndex `
                -IPAddress $HostIp -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
        } finally {
            $script:TemporaryIpAdded = $false
        }
    }
}

function Invoke-U03Curl {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $common = @(
        "--silent", "--show-error", "--connect-timeout", "3", "--max-time", "10",
        "--noproxy", "*", "--interface", $script:SourceIp,
        "-H", "Host: $DeviceHost",
        "-H", "Referer: $Referer",
        "-H", "X-Requested-With: XMLHttpRequest",
        "-H", "Accept: application/json, text/javascript, */*; q=0.01"
    )
    $output = @(& $script:CurlPath @common @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $text = $output -join [Environment]::NewLine
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "curl.exe failed with exit code ${exitCode}: $text"
    }
    return $text
}

function Get-U03RdToken {
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $stamp = [int64](([DateTime]::UtcNow - [DateTime]"1970-01-01").TotalMilliseconds)
            $url = "http://${DeviceIp}/goform/goform_get_cmd_process?isTest=false&cmd=RD&multi_data=1&_=$stamp"
            $response = Invoke-U03Curl -Arguments @($url)
            if ($response -match '"RD"\s*:\s*"([0-9A-Fa-f]{32})"') {
                return $Matches[1]
            }
        } catch {
            # The RNDIS adapter and Web UI can take a few seconds to become ready.
        }
        Start-Sleep -Milliseconds 500
    }
    throw "the U03 Web UI did not return a valid RD token"
}

function Get-U03Ad([string]$Rd) {
    $md5 = [Security.Cryptography.MD5]::Create()
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes($AuthSalt + $Rd)
        $hash = $md5.ComputeHash($bytes)
        return ([BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    } finally {
        $md5.Dispose()
    }
}

function Test-U03Success([string]$Response) {
    return $Response -match '"result"\s*:\s*"success"'
}

function Switch-U03ToModem {
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -eq $curl) {
        throw "curl.exe is required (included with current Windows 10 and Windows 11)"
    }
    $script:CurlPath = $curl.Source

    Initialize-U03Network
    try {
        Write-Log "waiting for the Web UI at $DeviceIp"
        $rd = Get-U03RdToken
        $ad = Get-U03Ad $rd

        Write-Log "enabling the persistent KDDI modem product mode"
        $data = "isTest=false&goformId=SET_PRODUCT_MODE_FOR_KDDI&debug_enable=1&AD=$ad"
        $response = Invoke-U03Curl -Arguments @(
            "-X", "POST", "--data", $data,
            "http://${DeviceIp}/goform/goform_set_cmd_process"
        )
        if (-not (Test-U03Success $response)) {
            throw "device rejected the mode switch: $response"
        }

        $rd = Get-U03RdToken
        $ad = Get-U03Ad $rd
        Write-Log "device accepted the setting; requesting reboot"
        $null = Invoke-U03Curl -AllowFailure -Arguments @(
            "-X", "POST", "--data", "isTest=false&goformId=REBOOT_DEVICE&AD=$ad",
            "http://${DeviceIp}/goform/goform_set_cmd_process"
        )
    } finally {
        Remove-U03TemporaryNetwork
    }

    Write-Log "waiting for modem USB ID 19d2:1481"
    $state = Wait-U03Product -Expected @($ProductModem)
    if ($null -eq $state) {
        throw "setting was accepted, but 19d2:1481 did not appear; unplug and reconnect the U03"
    }

    Write-Log "success: U03 is in persistent modem mode (19d2:1481)"
    $ports = @(Get-U03ComPorts $ProductModem)
    if ($ports.Count -gt 0) {
        $ports | ForEach-Object { Write-Output "$($_.Port)  $($_.Name)" }
    } else {
        Write-WarningLog "modem mode is active, but no COM ports are available; install the ZTE serial/modem driver"
    }
}

function Find-U03AtPort([string]$ProductId) {
    $wantedInterface = if ($ProductId -eq $ProductDiag) { "MI_02" } else { "MI_00" }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $ports = @(Get-U03ComPorts $ProductId)
        $preferred = @($ports | Where-Object { $_.InstanceId -match $wantedInterface } | Select-Object -First 1)
        if ($preferred.Count -gt 0) { return $preferred[0].Port }
        if ($ports.Count -eq 1) { return $ports[0].Port }
        Start-Sleep -Milliseconds 500
    }
    throw "could not find the U03 AT COM port; install the ZTE serial/modem driver"
}

function Send-U03At {
    param(
        [Parameter(Mandatory = $true)][IO.Ports.SerialPort]$Serial,
        [Parameter(Mandatory = $true)][string]$Command,
        [string]$RequiredText
    )

    $null = $Serial.ReadExisting()
    $Serial.Write("$Command`r")
    $response = ""
    $deadline = [DateTime]::UtcNow.AddSeconds(6)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $response += $Serial.ReadExisting()
        if ($response -match '(?m)^\s*(ERROR|\+CME ERROR:.*|\+CMS ERROR:.*)\s*$') {
            throw "$Command was rejected: $($Matches[1])"
        }
        if ($response -match '(?m)^\s*OK\s*$') {
            if ($RequiredText -and $response -notlike "*$RequiredText*") {
                throw "unexpected response to ${Command}: $response"
            }
            return
        }
    }
    throw "no OK response to ${Command}: $response"
}

function Switch-U03ToRndis([string]$ProductId) {
    $portName = Find-U03AtPort $ProductId
    Write-Log "using AT port: $portName"
    $serial = [IO.Ports.SerialPort]::new(
        $portName,
        115200,
        [IO.Ports.Parity]::None,
        8,
        [IO.Ports.StopBits]::One
    )
    $serial.Handshake = "None"
    $serial.DtrEnable = $false
    $serial.RtsEnable = $false
    $serial.ReadTimeout = 250
    $serial.WriteTimeout = 2000

    try {
        $serial.Open()
        Write-Log "disabling the persistent KDDI/CPE modem product mode"
        Send-U03At $serial "AT+ZCPE=o" "exit cpe mode result(0:FAIL 1:SUCCESS):1"
        Send-U03At $serial "AT+ZCDRUN=8" "Close autorun state result(0:FAIL 1:SUCCESS):1"
        Send-U03At $serial "AT+ZCDRUN=F" "exit download mode result(0:FAIL 1:SUCCESS):1"
        Write-Log "device accepted the settings; requesting reboot"
        Send-U03At $serial "AT+ZRST"
    } finally {
        if ($serial.IsOpen) { $serial.Close() }
        $serial.Dispose()
    }

    $state = Wait-U03Product -Expected @($ProductRndis, $ProductStorage)
    if ($null -eq $state) {
        throw "settings were accepted, but neither 19d2:1483 nor 19d2:1484 appeared"
    }
    if ($state.ProductId -eq $ProductStorage) {
        Write-Log "virtual CD-ROM appeared; waiting for the installed ZTE software to expose RNDIS"
        $state = Wait-U03Product -Expected @($ProductRndis) -TimeoutSeconds 12
    }
    if ($null -eq $state -or $state.ProductId -ne $ProductRndis) {
        throw "device stopped at 19d2:1484. Windows needs the original ZTE USB driver/software, or use the Linux tool once, to reach RNDIS"
    }
    Write-Log "success: U03 is in persistent RNDIS/Web UI mode (19d2:1483)"
}

if ($Version) {
    Write-Output $ToolVersion
    exit 0
}

if ($ToModem -and $ToRndis) {
    throw "-ToModem and -ToRndis cannot be used together"
}
if (-not $ToModem -and -not $ToRndis -and -not $Status) {
    $ToModem = $true
}
if ($Status -and ($ToModem -or $ToRndis)) {
    throw "-Status cannot be combined with a mode change"
}

if ($Status) {
    Show-U03Status
    exit 0
}

if (-not (Test-Administrator)) {
    throw "mode switching requires an Administrator PowerShell window"
}

$current = Get-U03State
Write-Log "found 19d2:$($current.ProductId.ToLowerInvariant()) ($($current.Mode))"

if ($ToModem) {
    if ($current.ProductId -eq $ProductModem) {
        Write-Log "already in modem mode; nothing to change"
        Show-U03Status
        exit 0
    }
    if ($current.ProductId -eq $ProductStorage) {
        $next = Wait-U03Product -Expected @($ProductRndis, $ProductModem) -TimeoutSeconds 12
        if ($null -eq $next) {
            throw "device is still 19d2:1484. Install/run the U03 ZTE USB software once to expose RNDIS, then rerun this script"
        }
        $current = $next
    }
    if ($current.ProductId -eq $ProductDiag) {
        throw "device is in factory/diagnostic mode; use -ToRndis first"
    }
    if ($current.ProductId -ne $ProductRndis) {
        throw "unexpected USB state: 19d2:$($current.ProductId)"
    }
    Confirm-U03Change "SWITCH" "This persistently changes the U03 USB composition and reboots it."
    Switch-U03ToModem
    exit 0
}

if ($current.ProductId -eq $ProductRndis) {
    Write-Log "already in RNDIS/Web UI mode; nothing to change"
    Show-U03Status
    exit 0
}
if ($current.ProductId -eq $ProductStorage) {
    throw "device is in virtual CD-ROM mode; install/run the U03 ZTE USB software to expose RNDIS"
}
if ($current.ProductId -ne $ProductModem -and $current.ProductId -ne $ProductDiag) {
    throw "unexpected USB state: 19d2:$($current.ProductId)"
}
Confirm-U03Change "RNDIS" "This restores the persistent U03 RNDIS/Web UI USB composition and reboots it."
Switch-U03ToRndis $current.ProductId
