[CmdletBinding()]
param (
    [string]
    [ValidateSet('https://smt-orders-e103ef-api.azurewebsites.net', 'http://localhost:8080')]
    $base = 'https://smt-orders-e103ef-api.azurewebsites.net'
)
$tenant = '76c1800f-d619-4981-9230-358dbc4396b6'
$scope  = 'api://754d122b-cb6d-45be-940a-5f6646b23f8f/access_as_user'

Write-Host "Checking Health... " -NoNewline -ForegroundColor Cyan
Invoke-RestMethod "$base/health" -Headers $headers | ConvertTo-Json -Compress | Write-Host

if ($null -eq $env:token ) {
    Write-Host "No token found, logging in to Azure..." -NoNewline -ForegroundColor Cyan
    az login --tenant $tenant --scope $scope
    $env:token = az account get-access-token --tenant $tenant --scope $scope --query accessToken -o tsv
}
$headers = @{ Authorization = "Bearer $env:token" }

Write-Host "Getting Components... " -NoNewline -ForegroundColor Cyan
$existingComponents = Invoke-RestMethod "$base/api/components" -Headers $headers
$existingComponents | ConvertTo-Json -Compress | Write-Host
Write-Host

$components = @("0201", "0402", "0603", "0805", "1206") |
    ForEach-Object {
        $size = $_
        @("0", "10", "100", "1k", "10k", "100k", "1M") |
            ForEach-Object {
                $value = $_
                @{
                    partNumber = "R$value-$size"
                    name = "Resistor $value Ohm"
                    description = "A $value ohm resistor, $size package"
                    physicalStock = Get-Random -Minimum 1000 -Maximum 10000
                }
            }
    } |
    Where-Object { $existingComponents.partNumber -notcontains $_.partNumber }

$components | ForEach-Object {
    Write-Host "Inserting: $($_.partNumber)... " -ForegroundColor Cyan -NoNewline
    Invoke-RestMethod "$base/api/components" -ContentType "application/json" -Headers $headers -Method Post -Body ($_ | ConvertTo-Json -Compress)  | ConvertTo-Json -Compress | Write-Host
}
$components = Invoke-RestMethod "$base/api/components" -Headers $headers

Write-Host "Getting Boards... " -NoNewline -ForegroundColor Cyan
$existingBoards = Invoke-RestMethod "$base/api/boards" -Headers $headers
$existingBoards | ConvertTo-Json -Compress | Write-Host
Write-Host

$boards = @("Arduino Uno", "Raspberry Pi 4", "ESP32 DevKit", "STM32 Nucleo", "BeagleBone Black") |
    ForEach-Object {
        $name = $_

        @{
            partNumber = $name -replace ' ', '-'
            name = $name
            description = "A $name development board"
            lengthMm = Get-Random -Minimum 50 -Maximum 150
            widthMm = Get-Random -Minimum 30 -Maximum 100
            recipe = $components |
                Foreach-Object {
                    [pscustomobject]@{
                        component = $_
                        random = Get-Random -Minimum 0 -Maximum 1000
                    }
                } |
                Sort-Object -Property random |
                ForEach-Object {
                    [pscustomobject]@{
                        componentId = $_.component.Id
                        quantityPerBoard = Get-Random -Minimum 1 -Maximum 10
                    }
                } |
                Select-Object -First (Get-Random -Minimum 5 -Maximum 20)
        }
    } |
    Where-Object { $existingBoards.partNumber -notcontains $_.partNumber }

$boards | ForEach-Object {
    Write-Host "Inserting: $($_.partNumber)... " -ForegroundColor Cyan -NoNewline
    Invoke-RestMethod "$base/api/boards" -ContentType "application/json" -Headers $headers -Method Post -Body ($_ | ConvertTo-Json -Compress)  | ConvertTo-Json -Compress | Write-Host
}