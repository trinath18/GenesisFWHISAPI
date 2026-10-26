# Legacy inventory summary

Generated from `C:\GenesisFIS\FISApplication\src\FIS VB`. Static analysis; see tools/inventory/build_inventory.py for rules and limits.

## Totals

- Forms: 351; modules (.bas): 12; classes: 2
- Lines of code (non-blank, incl. designer): 1,155,446
- Forms opened directly from a menu: 184; reachable only via another form: 44; not reachable from any menu (dead-code candidates): 123
- Stored procedures referenced by code: 269 of 745 in the databases (476 never referenced by a string literal)
- Tables/views referenced: 392
- Forms using Crystal reports: 18; Data Reports: 4; Excel: 63; Word: 29; file I/O: 41; MAPI: 2
- Clone candidates (>=80% same tables/procs, similar size): 81 forms in 36 groups. If each group became one parameterised screen, about 306 unique screens would remain.

## Size buckets (forms)

- S: 91
- M: 156
- L: 75
- XL: 29

## By module (first menu path)

| Module | Forms | LOC | XL | With Excel/Word | With reports |
|---|---|---|---|---|---|
| (not on menu / helper) | 123 | 259,928 | 5 | 21 | 17 |
| Membership | 23 | 232,539 | 6 | 3 | 2 |
| Claim | 16 | 198,170 | 8 | 6 | 0 |
| Worksheet | 27 | 123,548 | 5 | 5 | 1 |
| Case | 32 | 88,706 | 3 | 10 | 0 |
| Payment | 25 | 49,225 | 0 | 10 | 0 |
| Claim Bordx | 27 | 45,498 | 0 | 8 | 2 |
| Maintenance | 18 | 37,227 | 1 | 0 | 0 |
| Receipt | 14 | 36,105 | 0 | 7 | 0 |
| Invoice | 16 | 26,555 | 1 | 1 | 0 |
| MCOFees | 11 | 16,574 | 0 | 5 | 0 |
| A/C Export | 10 | 16,142 | 0 | 0 | 0 |
| File | 3 | 5,919 | 0 | 0 | 0 |
| Statistic | 6 | 2,488 | 0 | 1 | 0 |

## Top 15 stored procedures by number of forms using them

| Procedure | Forms |
|---|---|
| SearchPayor | 91 |
| SearchHOS | 27 |
| SearchHISTableRec | 24 |
| SearchState | 17 |
| SearchINS | 17 |
| SearchHLTType | 13 |
| SearchPayorHLTType | 13 |
| SearchHospGrp | 9 |
| SearchComboListing | 9 |
| Upload_MBMCovPersonsCis | 9 |
| SUN_HIS_Posting | 9 |
| SearchPolStatus | 8 |
| SearchProductCategory | 8 |
| ClaimReport | 8 |
| SearchMBMCrossRef | 8 |

## Top 15 tables by number of forms using them

| Table | Forms |
|---|---|
| pay | 83 |
| mbmcoveredpersons | 63 |
| claim | 52 |
| mbm | 51 |
| bordx | 42 |
| hos | 36 |
| mbmothers | 32 |
| claiminvoice | 32 |
| pln | 29 |
| cas | 29 |
| mbmcrossreference | 29 |
| status | 25 |
| annuallimit | 20 |
| companygrp | 20 |
| mbmlifetime | 19 |

## Databases touched (forms)

- Main: 245
- Worksheet: 132
- Maintenance: 125
- Payment: 53
- CIS: 20
- Neptune: 19
- Pluto: 8
- Archive: 6
