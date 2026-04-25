param([string]$ParamsFile = "infra/main.parameters.json")

$p      = Get-Content $ParamsFile -Raw | ConvertFrom-Json
$server = "sql-mdh-qprfvryy.database.windows.net"
$db     = "MDH"
$login  = $p.parameters.sqlAdminLogin.value
$pass   = $p.parameters.sqlAdminPassword.value

Write-Host "Table check on $server / $db (login: $login)"
$q = "SELECT s.name + '.' + t.name AS full_name FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id ORDER BY 1;"
& sqlcmd -S "$server,1433" -d $db -U $login -P $pass -Q $q -l 90
