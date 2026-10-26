"""Prints the data sources (databases, procedures, tables/views) of the given legacy forms from forms.csv."""
import csv
import sys

inv = sys.argv[1]
names = [n.lower() for n in sys.argv[2:]]
rows = {r["name"].lower(): r for r in csv.DictReader(open(f"{inv}/forms.csv", encoding="utf-8-sig"))}
for n in names:
    r = rows.get(n)
    if not r:
        print(f"{n}: not found")
        continue
    print(f"{r['name']:<26} loc={r['loc']:<6} dbs={r['databases']}")
    print(f"    sps   : {r['stored_procs'][:200]}")
    print(f"    tables: {r['tables'][:300]}")
