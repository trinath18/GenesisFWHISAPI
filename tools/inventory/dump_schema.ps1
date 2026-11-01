# Exports the schema of the restored FWHS databases into docs/inventory/schema so it can be read without a database connection:
#   <db>.routines.sql  - full source of every stored procedure, view, function and trigger
#   <db>.columns.tsv   - object, type (U table / V view), column, data type, max length (bytes), nullable, column id
#   <db>.tables.tsv    - table, row count, primary key columns
#   dump.log           - what happened (read this if a file is missing)
# Usage (from the GenesisFISAPI folder):  powershell -ExecutionPolicy Bypass -File tools\inventory\dump_schema.ps1
# Read-only: it only runs SELECTs against the sys.* catalog views. Uses the .NET SQL client built into Windows PowerShell (no sqlcmd needed),
# Windows authentication to the local server. Written for old compatibility levels (no STRING_AGG).
param(
    [string] $Server = 'localhost',
    [string[]] $Databases = @('FWHSHISDB', 'FWHSHISMaintenance', 'FWHSHISWorksheet', 'FWHSHISPayment', 'FWHSHISARC')
)
$out = Join-Path $PSScriptRoot '..\..\docs\inventory\schema'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'dump.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Log([string] $line) { Write-Host $line; [System.IO.File]::AppendAllText($log, ((Get-Date).ToString('s') + ' ' + $line + "`r`n"), $utf8) }
Set-Content -Path $log -Value '' -Encoding ASCII

function Query([string] $db, [string] $sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection("Server=$Server;Database=$db;Integrated Security=True;Connect Timeout=30")
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 600
        $table = New-Object System.Data.DataTable
        $table.Load($cmd.ExecuteReader())
        return ,$table
    } finally { $cn.Dispose() }
}

$routinesSql = @"
select o.type_desc as kind, s.name as sch, o.name as name, m.definition as body
from sys.objects o join sys.schemas s on s.schema_id = o.schema_id left join sys.sql_modules m on m.object_id = o.object_id
where o.type in ('P', 'V', 'FN', 'IF', 'TF', 'TR') and o.is_ms_shipped = 0
order by o.type, o.name
"@
$columnsSql = @"
select o.name as obj, o.type as kind, c.name as col, t.name as typ, c.max_length as len, c.is_nullable as nul, c.column_id as id
from sys.columns c join sys.objects o on o.object_id = c.object_id join sys.types t on t.user_type_id = c.user_type_id
where o.type in ('U', 'V') and o.is_ms_shipped = 0
order by o.name, c.column_id
"@
$tablesSql = @"
select o.name as obj,
       (select sum(p.rows) from sys.partitions p where p.object_id = o.object_id and p.index_id in (0, 1)) as row_count,
       isnull(stuff((select ',' + c.name from sys.indexes i join sys.index_columns ic on ic.object_id = i.object_id and ic.index_id = i.index_id
               join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where i.object_id = o.object_id and i.is_primary_key = 1
               order by ic.key_ordinal for xml path('')), 1, 1, ''), '') as pk
from sys.objects o where o.type = 'U' and o.is_ms_shipped = 0 order by o.name
"@

foreach ($db in $Databases) {
    Log "== $db"
    try {
        $rows = Query $db $routinesSql
        $sb = New-Object System.Text.StringBuilder
        foreach ($r in $rows.Rows) {
            [void]$sb.Append("`r`n-- ==== $($r.kind) $($r.sch).$($r.name)`r`n")
            if ($r.body -is [System.DBNull]) { [void]$sb.Append("-- (definition not available: encrypted or no permission)`r`n") } else { [void]$sb.Append($r.body).Append("`r`n") }
        }
        [System.IO.File]::WriteAllText((Join-Path $out "$db.routines.sql"), $sb.ToString(), $utf8)
        Log ("   routines: {0} objects, {1:N0} KB" -f $rows.Rows.Count, ($sb.Length / 1KB))
    } catch { Log "   routines FAILED: $($_.Exception.Message)" }
    try {
        $rows = Query $db $columnsSql
        $lines = foreach ($r in $rows.Rows) { "{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}" -f $r.obj, $r.kind.Trim(), $r.col, $r.typ, $r.len, [int]$r.nul, $r.id }
        [System.IO.File]::WriteAllLines((Join-Path $out "$db.columns.tsv"), [string[]]$lines, $utf8)
        Log "   columns: $($rows.Rows.Count)"
    } catch { Log "   columns FAILED: $($_.Exception.Message)" }
    try {
        $rows = Query $db $tablesSql
        $lines = foreach ($r in $rows.Rows) { "{0}`t{1}`t{2}" -f $r.obj, $r.row_count, $r.pk }
        [System.IO.File]::WriteAllLines((Join-Path $out "$db.tables.tsv"), [string[]]$lines, $utf8)
        Log "   tables: $($rows.Rows.Count)"
    } catch { Log "   tables FAILED: $($_.Exception.Message)" }
}
Log "Done: $out"
