# Describes tables/views in the restored FWHS databases: columns, row count, primary key, triggers, and optionally sample rows.
# Usage: .\describe.ps1 -Database FWHSHISDB -Objects Company,PanelHospitals [-Sample 3] [-Find 'Compan']
param(
    [Parameter(Mandatory)] [string] $Database,
    [string[]] $Objects = @(),
    [int] $Sample = 0,
    [string] $Find = ''
)
function Run($q) { (sqlcmd -S localhost -E -C -d $Database -W -h -1 -Q "set nocount on; $q") -join "`n" }
if ($Find) {
    "== objects in $Database matching '$Find'"
    Run "select o.name + ' (' + o.type + ')' collate database_default from sys.objects o where o.name like '%$Find%' and o.type in ('U','V','P') order by o.name"
}
foreach ($o in $Objects) {
    "== $Database.$o"
    "columns: " + (Run "select string_agg(c.name + ':' + t.name + case when t.name like '%char%' then '(' + cast(c.max_length as varchar) + ')' else '' end + case c.is_nullable when 1 then '?' else '' end, ', ') from sys.columns c join sys.types t on t.user_type_id = c.user_type_id where c.object_id = object_id('$o')")
    "rows: " + (Run "select count(*) from [$o]")
    "pk: " + (Run "select string_agg(c.name, ',') from sys.index_columns ic join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id where i.is_primary_key = 1 and i.object_id = object_id('$o')")
    "triggers: " + (Run "select string_agg(name, ',') from sys.triggers where parent_id = object_id('$o')")
    if ($Sample -gt 0) { sqlcmd -S localhost -E -C -d $Database -W -Q "set nocount on; select top $Sample * from [$o]" }
}

