"""
Static inventory of the legacy VB6 application.

Usage:  python build_inventory.py <vb-source-dir> <catalog-dir> <output-dir>

- <catalog-dir> holds one TSV per database: db<TAB>kind<TAB>name  (T table, V view, P procedure, F function)
- Output: forms.csv/json, sp_usage.csv, table_usage.csv, reports.csv, menu_map.csv, clone_groups.csv, summary.md

This is regex based static analysis: it is accurate for names written as string literals in SQL, and can miss
SQL assembled dynamically from variables. Treat the output as a work list, not as a proof of absence.
"""
import csv
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

SRC, CATALOG, OUT = (Path(a) for a in sys.argv[1:4])
OUT.mkdir(parents=True, exist_ok=True)

ENC = "cp1252"
SQL_KW = re.compile(r"\b(select|from|join|insert\s+into|update|delete|exec(?:ute)?|into|where|set)\b", re.I)
STR_RE = re.compile(r'"((?:[^"]|"")*)"')
CONN_DB = {
    "medixmasterconn": "Main", "medixmasterconnA": "Archive", "medixmasterconnWS": "Worksheet",
    "medixmasterconnPayment": "Payment", "medixmasterconnMaint": "Maintenance",
    "MediConnCISDB": "CIS", "medixmasterconnPluto": "Pluto", "medixmasterconnWSPluto": "Pluto",
    "medixneptuneconn": "Neptune", "medixneptuneconnA": "Neptune", "medixneptuneconnWS": "Neptune",
    "medixneptuneconnPayment": "Neptune",
}
CONN_RE = re.compile(r"\b(" + "|".join(sorted(CONN_DB, key=len, reverse=True)) + r")\b")

objs = {"T": defaultdict(set), "V": defaultdict(set), "P": defaultdict(set), "F": defaultdict(set)}
for tsv in CATALOG.glob("*.tsv"):
    for line in tsv.read_text(encoding="utf-8", errors="ignore").splitlines():
        parts = line.split("\t")
        if len(parts) == 3:
            db, kind, name = parts
            if kind in objs and name:
                objs[kind][name.lower()].add(db)

def word_alt(names):
    names = sorted((re.escape(n) for n in names), key=len, reverse=True)
    return re.compile(r"(?<![\w.])(?:\w+\.\.?|\w+\.dbo\.|dbo\.)?(" + "|".join(names) + r")(?!\w)", re.I) if names else None

TABLE_RE = word_alt(list(objs["T"]) + list(objs["V"]))
PROC_RE = word_alt(list(objs["P"]))
FUNC_RE = word_alt(list(objs["F"]))

def read(path):
    return path.read_text(encoding=ENC, errors="replace").splitlines()

def logical_lines(lines):
    """Join VB line continuations ( _ ) and drop whole-line comments. Returns (code_lines, loc, comment_lines)."""
    out, buf, comments = [], "", 0
    for raw in lines:
        s = raw.strip()
        if not buf and (s.startswith("'") or s.lower().startswith("rem ")):
            comments += 1
            continue
        if s.endswith(" _"):
            buf += s[:-2] + " "
            continue
        out.append(buf + s)
        buf = ""
    if buf:
        out.append(buf)
    return out, comments

def header_info(lines):
    caption = None
    controls = defaultdict(int)
    in_form = False
    for ln in lines[:4000]:
        m = re.match(r"\s*Begin\s+([\w.]+)\s+(\w+)", ln)
        if m:
            t = m.group(1)
            controls[t] += 1
            if t == "VB.Form" or t == "VB.MDIForm":
                in_form = True
        if caption is None and in_form:
            c = re.match(r'\s*Caption\s*=\s*"(.*)"', ln)
            if c:
                caption = c.group(1)
        if ln.startswith("Attribute VB_Name"):
            break
    return caption, dict(controls)

# --- code files ---
files = sorted([p for p in SRC.iterdir() if p.suffix.lower() in (".frm", ".bas", ".cls") and p.is_file()])
dsr_names = {p.stem.lower(): p.name for p in SRC.iterdir() if p.suffix.lower() == ".dsr"}
dsr_re = re.compile(r"(?<![\w])(" + "|".join(re.escape(n) for n in sorted(dsr_names, key=len, reverse=True)) + r")(?!\w)", re.I) if dsr_names else None
form_names = {p.stem.lower(): p.stem for p in files if p.suffix.lower() == ".frm"}
form_ref_re = re.compile(r"(?<![\w])(" + "|".join(re.escape(n) for n in sorted(form_names, key=len, reverse=True)) + r")(?!\w)", re.I)

rows, sp_usage, tbl_usage, reports = [], defaultdict(set), defaultdict(set), []
form_sets = {}
opens = defaultdict(set)

for p in files:
    lines = read(p)
    code, comments = logical_lines(lines)
    caption, controls = header_info(lines)
    in_code = False
    body = []
    for ln in code:
        if ln.startswith("Attribute VB_Name"):
            in_code = True
            continue
        if in_code:
            body.append(ln)
    text = "\n".join(body)

    conns = sorted({CONN_DB[m] for m in CONN_RE.findall(text)})
    sps, tables, funcs = set(), set(), set()
    sql_stmt = 0
    for ln in body:
        strs = [s.replace('""', '"') for s in STR_RE.findall(ln)]
        if not strs:
            continue
        joined = " ".join(strs)
        is_sql = bool(SQL_KW.search(joined)) or bool(re.match(r"^\w+\s*=\s*\w+\s*&\s*\"", ln))
        if SQL_KW.search(joined):
            sql_stmt += 1
        if PROC_RE:
            for s in strs:
                st = s.strip()
                m = re.match(r"^(?:exec(?:ute)?\s+)?(?:\w+\.)*(\w+)\b", st, re.I)
                if m and m.group(1).lower() in objs["P"] and (re.match(r"^exec", st, re.I) or re.fullmatch(r"\w+", st)):
                    sps.add(m.group(1))
            for m in PROC_RE.finditer(joined):
                if re.search(r"exec(?:ute)?\s+(?:\w+\.\.?|\w+\.dbo\.|dbo\.)?" + re.escape(m.group(1)), joined, re.I):
                    sps.add(m.group(1))
        if is_sql and TABLE_RE:
            for m in TABLE_RE.finditer(joined):
                name = m.group(1)
                if len(name) >= 3:
                    tables.add(name)
        if is_sql and FUNC_RE:
            for m in FUNC_RE.finditer(joined):
                funcs.add(m.group(1))
    # CommandText = "proc" with adCmdStoredProc is covered by the fullmatch rule above.

    rpt = set()
    for m in re.finditer(r"ReportFileName\s*=\s*([^\n]+)", text, re.I):
        lit = re.findall(r'"([^"]+\.rpt)"', m.group(1), re.I)
        rpt.update(l.replace("\\", "/").split("/")[-1] for l in lit) if lit else rpt.add("(dynamic path)")
    for m in re.finditer(r'"([^"]*\.rpt)"', text, re.I):
        rpt.add(m.group(1).replace("\\", "/").split("/")[-1])
    dsr = sorted({dsr_names[m.lower()] for m in dsr_re.findall(text)}) if dsr_re else []

    count = lambda pat: len(re.findall(pat, text, re.I))
    refs = {form_names[m.lower()] for m in form_ref_re.findall(text)} - {p.stem}
    opens[p.stem] = refs

    for s in sps: sp_usage[s].add(p.stem)
    for t in tables: tbl_usage[t.lower()].add(p.stem)
    for r in rpt: reports.append((p.stem, "crystal", r))
    for r in dsr: reports.append((p.stem, "datareport", r))

    grid_types = ("MSFlexGridLib", "MSDataGridLib", "MSComctlLib.ListView", "MSDBGrid", "TrueDBGrid")
    grids = sum(v for k, v in controls.items() if any(g in k for g in grid_types))
    loc = len([l for l in lines if l.strip()])
    rows.append({
        "name": p.stem, "kind": p.suffix.lower().lstrip("."), "caption": caption or "",
        "loc": loc, "comment_lines": comments, "controls": sum(controls.values()), "grids": grids,
        "databases": ";".join(conns), "sql_statements": sql_stmt,
        "stored_procs": ";".join(sorted(sps, key=str.lower)), "tables": ";".join(sorted(tables, key=str.lower)),
        "functions": ";".join(sorted(funcs, key=str.lower)),
        "crystal_reports": ";".join(sorted(rpt)), "data_reports": ";".join(dsr),
        "excel": count(r"Excel\.Application|CreateObject\(\"Excel"), "word": count(r"Word\.Application|CreateObject\(\"Word"),
        "file_io": count(r"\bOpen\s+.+\s+For\s+(Input|Output|Append|Binary)"), "mapi": count(r"MAPI(Session|Messages)|Outlook"),
        "shell": count(r"\bShell\s*\(|ShellExecute"), "win32_api": count(r"^\s*(Private |Public )?Declare\s+(Function|Sub)"),
        "transactions": count(r"\.BeginTrans"), "timers": controls.get("VB.Timer", 0),
        "opens_forms": ";".join(sorted(refs)), "menus": "", "module": "",
    })
    form_sets[p.stem] = (set(x.lower() for x in sps) | set(x.lower() for x in tables), loc)

# --- menus from mdiMain ---
menu_rows = []
mdi = SRC / "mdiMain.frm"
form_menus = defaultdict(list)
if mdi.exists():
    lines = read(mdi)
    stack, items = [], []
    for i, ln in enumerate(lines):
        m = re.match(r"^(\s*)Begin\s+VB\.Menu\s+(\w+)", ln)
        if m:
            depth = len(m.group(1))
            cap = ""
            for j in range(i + 1, min(i + 8, len(lines))):
                c = re.match(r'\s*Caption\s*=\s*"(.*)"', lines[j])
                if c:
                    cap = c.group(1).replace("&", "").strip()
                    break
            while stack and stack[-1][0] >= depth:
                stack.pop()
            stack.append((depth, cap))
            items.append((m.group(2), cap, " > ".join(c for _, c in stack if c), len(stack)))
    code, _ = logical_lines(lines)
    handlers = {}
    cur, buf = None, []
    for ln in code:
        m = re.match(r"(?:Private|Public)?\s*Sub\s+(\w+)_Click\b", ln, re.I)
        if m:
            cur, buf = m.group(1).lower(), []
            continue
        if cur and re.match(r"End Sub", ln, re.I):
            handlers[cur] = "\n".join(buf)
            cur = None
        elif cur:
            buf.append(ln)
    for name, cap, path, level in items:
        body = handlers.get(name.lower(), "")
        targets = sorted({form_names[m.lower()] for m in form_ref_re.findall(body)})
        access = sorted({int(a) for a in re.findall(r"CheckAccess\((\d+)\)", body)})
        menu_rows.append({"menu": name, "caption": cap, "path": path, "level": level, "opens": ";".join(targets),
                          "access_position": ";".join(map(str, access))})
        for t in targets:
            form_menus[t].append(path)
for r in rows:
    r["menus"] = " | ".join(sorted(set(form_menus.get(r["name"], []))))
    first = (form_menus.get(r["name"]) or [""])[0]
    r["module"] = first.split(" > ")[0] if first else ""

# --- clone groups: same module-ish family with near-identical table/SP sets and similar size ---
cl_rows = []
names = [r["name"] for r in rows if r["kind"] == "frm" and form_sets[r["name"]][0]]
used = set()
gid = 0
for a in names:
    if a in used: continue
    sa, la = form_sets[a]
    grp = [a]
    for b in names:
        if b == a or b in used: continue
        sb, lb = form_sets[b]
        j = len(sa & sb) / max(1, len(sa | sb))
        if j >= 0.8 and 0.7 <= (lb / max(la, 1)) <= 1.43:
            grp.append(b)
    if len(grp) > 1:
        gid += 1
        used.update(grp)
        for g in grp:
            cl_rows.append({"group": f"G{gid:03d}", "form": g, "loc": form_sets[g][1]})
clone_forms = {r["form"] for r in cl_rows}
clone_groups = len({r["group"] for r in cl_rows})

# transitive reachability from menu entry points (forms opened by other forms)
reach, frontier = {r["name"]: r["name"] for r in rows if r["menus"]}, [r["name"] for r in rows if r["menus"]]
while frontier:
    cur = frontier.pop()
    for nxt in opens.get(cur, ()):
        if nxt not in reach:
            reach[nxt] = reach[cur]
            frontier.append(nxt)
for r in rows:
    r["reachable_from_menu"] = "direct" if r["menus"] else ("via " + reach[r["name"]] if r["name"] in reach else ("" if r["kind"] != "frm" else "NO"))
    if not r["module"] and r["name"] in reach:
        first = (form_menus.get(reach[r["name"]]) or [""])[0]
        r["module"] = first.split(" > ")[0] if first else ""

def bucket(r):
    score = r["loc"] + 200 * (r["excel"] + r["word"]) + 30 * r["sql_statements"]
    return "S" if score < 800 else "M" if score < 3000 else "L" if score < 10000 else "XL"
for r in rows:
    r["size"] = bucket(r)
    r["clone_candidate"] = "Y" if r["name"] in clone_forms else ""

def write_csv(name, data, fields):
    with open(OUT / name, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        w.writerows(data)

fields = list(rows[0].keys())
write_csv("forms.csv", rows, fields)
(OUT / "forms.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
write_csv("sp_usage.csv", [{"stored_proc": s, "databases": ";".join(sorted(objs["P"].get(s.lower(), []))), "form_count": len(f), "forms": ";".join(sorted(f))} for s, f in sorted(sp_usage.items(), key=lambda x: -len(x[1]))], ["stored_proc", "databases", "form_count", "forms"])
write_csv("table_usage.csv", [{"table": t, "databases": ";".join(sorted((objs["T"].get(t) or objs["V"].get(t) or []))), "form_count": len(f), "forms": ";".join(sorted(f))} for t, f in sorted(tbl_usage.items(), key=lambda x: -len(x[1]))], ["table", "databases", "form_count", "forms"])
write_csv("reports.csv", [{"form": f, "type": t, "report": r} for f, t, r in sorted(set(reports))], ["form", "type", "report"])
write_csv("menu_map.csv", menu_rows, ["menu", "caption", "path", "level", "opens", "access_position"])
write_csv("clone_groups.csv", cl_rows, ["group", "form", "loc"])

# --- summary ---
frm = [r for r in rows if r["kind"] == "frm"]
by_mod = defaultdict(list)
for r in frm: by_mod[r["module"] or "(not on menu / helper)"].append(r)
all_sp_used = set(sp_usage)
all_sp = {n for n in objs["P"]}
mem = lambda items, k: sum(1 for r in items if r[k])
lines_out = [
    "# Legacy inventory summary", "",
    f"Generated from `{SRC}`. Static analysis; see tools/inventory/build_inventory.py for rules and limits.", "",
    "## Totals", "",
    f"- Forms: {len(frm)}; modules (.bas): {sum(1 for r in rows if r['kind']=='bas')}; classes: {sum(1 for r in rows if r['kind']=='cls')}",
    f"- Lines of code (non-blank, incl. designer): {sum(r['loc'] for r in rows):,}",
    f"- Forms opened directly from a menu: {sum(1 for r in frm if r['menus'])}; reachable only via another form: {sum(1 for r in frm if r['reachable_from_menu'].startswith('via'))}; not reachable from any menu (dead-code candidates): {sum(1 for r in frm if r['reachable_from_menu']=='NO')}",
    f"- Stored procedures referenced by code: {len(all_sp_used)} of {len(all_sp)} in the databases ({len(all_sp)-len(all_sp_used)} never referenced by a string literal)",
    f"- Tables/views referenced: {len(tbl_usage)}",
    f"- Forms using Crystal reports: {sum(1 for r in frm if r['crystal_reports'])}; Data Reports: {sum(1 for r in frm if r['data_reports'])}; Excel: {mem(frm,'excel')}; Word: {mem(frm,'word')}; file I/O: {mem(frm,'file_io')}; MAPI: {mem(frm,'mapi')}",
    f"- Clone candidates (>=80% same tables/procs, similar size): {len(clone_forms)} forms in {clone_groups} groups. If each group became one parameterised screen, about {len(frm) - len(clone_forms) + clone_groups} unique screens would remain.",
    "", "## Size buckets (forms)", "",
]
for b in ("S", "M", "L", "XL"):
    lines_out.append(f"- {b}: {sum(1 for r in frm if r['size']==b)}")
lines_out += ["", "## By module (first menu path)", "", "| Module | Forms | LOC | XL | With Excel/Word | With reports |", "|---|---|---|---|---|---|"]
for mod, items in sorted(by_mod.items(), key=lambda x: -sum(r["loc"] for r in x[1])):
    lines_out.append(f"| {mod} | {len(items)} | {sum(r['loc'] for r in items):,} | {sum(1 for r in items if r['size']=='XL')} | {sum(1 for r in items if r['excel'] or r['word'])} | {sum(1 for r in items if r['crystal_reports'] or r['data_reports'])} |")
lines_out += ["", "## Top 15 stored procedures by number of forms using them", "", "| Procedure | Forms |", "|---|---|"]
for s, f in sorted(sp_usage.items(), key=lambda x: -len(x[1]))[:15]:
    lines_out.append(f"| {s} | {len(f)} |")
lines_out += ["", "## Top 15 tables by number of forms using them", "", "| Table | Forms |", "|---|---|"]
for t, f in sorted(tbl_usage.items(), key=lambda x: -len(x[1]))[:15]:
    lines_out.append(f"| {t} | {len(f)} |")
lines_out += ["", "## Databases touched (forms)", ""]
dbc = defaultdict(int)
for r in frm:
    for d in filter(None, r["databases"].split(";")): dbc[d] += 1
for d, n in sorted(dbc.items(), key=lambda x: -x[1]): lines_out.append(f"- {d}: {n}")
(OUT / "summary.md").write_text("\n".join(lines_out) + "\n", encoding="utf-8")
print("\n".join(lines_out[:40]))
