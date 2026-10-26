"""
Lists, for the given stored procedures, each branch's SELECT column list and source view/table.
Usage: python proc_sources.py <database> <proc> [<proc> ...]
Requires sqlcmd on PATH (Windows auth to localhost).
"""
import re
import subprocess
import sys

db, procs = sys.argv[1], sys.argv[2:]
for proc in procs:
    out = subprocess.run(
        ["sqlcmd", "-S", "localhost", "-E", "-C", "-d", db, "-y", "0", "-Q", f"set nocount on; select object_definition(object_id('{proc}'))"],
        capture_output=True, text=True).stdout
    print(f"=== {db}.{proc}  ({len(out)} chars)")
    for m in re.finditer(r"(?is)@strAppendCase\s*=\s*(\d+).*?set\s+@l_sqlstr\w*\s*=\s*N'(.*?)'", out):
        sql = re.sub(r"\s+", " ", m.group(2)).strip()
        frm = re.search(r"(?i)\bfrom\s+([\w.\[\]]+)", sql)
        print(f"  case {m.group(1):<3} from {frm.group(1) if frm else '?':<34} {sql[:150]}")
    if "strAppendCase" not in out:
        print("  " + re.sub(r"\s+", " ", out)[:700])
