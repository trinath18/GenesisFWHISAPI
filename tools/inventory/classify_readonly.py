"""
Classifies legacy forms as read-only or write, using the inventory output plus a scan for write operations.

Usage: python classify_readonly.py <vb-source-dir> <inventory-dir>
Writes <inventory-dir>/readonly_candidates.csv
"""
import csv
import re
import sys
from pathlib import Path

SRC, INV = Path(sys.argv[1]), Path(sys.argv[2])
WRITE = re.compile(
    r"(?i)\b(insert\s+into|update\s+\w[\w.]*\s+set|delete\s+from|truncate\s+table)\b|\.AddNew\b|\.Update\b(?!\w)|\.Delete\b|\.BeginTrans\b|"
    r"\bexec(?:ute)?\s+\w*(?:Insert|Update|Delete|Save|Post|Reg|Add|Gen|Create|Cancel|Reverse|Reissue|Send|Approve)\w*"
)
EXPORT = re.compile(r"(?i)Open\s+.+\s+For\s+(Output|Append)|\bKill\b|FileCopy|MkDir")

rows = list(csv.DictReader(open(INV / "forms.csv", encoding="utf-8-sig")))
out = []
for r in rows:
    if r["kind"] != "frm":
        continue
    text = (SRC / f"{r['name']}.frm").read_text(encoding="cp1252", errors="replace")
    code = "\n".join(l for l in text.splitlines() if not l.strip().startswith("'"))
    writes = sorted({m.group(0).strip()[:40] for m in WRITE.finditer(code)})
    export = bool(EXPORT.search(code))
    out.append({
        "name": r["name"], "caption": r["caption"], "module": r["module"], "menus": r["menus"], "reachable": r["reachable_from_menu"],
        "size": r["size"], "loc": r["loc"], "write_ops": len(writes), "write_samples": "; ".join(writes[:4]), "file_export": "Y" if export else "",
        "excel": r["excel"], "word": r["word"], "crystal": r["crystal_reports"], "tables": r["tables"][:160], "sps": r["stored_procs"][:160],
        "class": "READ-ONLY" if not writes else "WRITE",
    })
with open(INV / "readonly_candidates.csv", "w", newline="", encoding="utf-8-sig") as f:
    w = csv.DictWriter(f, fieldnames=list(out[0].keys()))
    w.writeheader()
    w.writerows(out)

menu = [o for o in out if o["menus"]]
ro = [o for o in menu if o["class"] == "READ-ONLY"]
print(f"menu forms: {len(menu)}  read-only: {len(ro)}  write: {len(menu) - len(ro)}")
for o in sorted(ro, key=lambda x: (x["module"], x["menus"])):
    print(f"{o['module']:<14} {o['name']:<28} loc={o['loc']:<6} {o['size']:<3} xl={o['excel']:<2} wd={o['word']:<2} exp={o['file_export']:<1} | {o['menus'][:70]}")
