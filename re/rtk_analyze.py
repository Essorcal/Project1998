import re

SQL = 'rtk_unkmc_RTK-Server/database/2020-09-02-21-55-01_RTK.sql.bak'
sql = open(SQL, encoding='latin1').read()
print("file size:", len(sql), "bytes\n")

# --- all tables: schema + column list ---
tables = {}
for m in re.finditer(r"CREATE TABLE `(\w+)` \((.*?)\n\)\s*ENGINE", sql, re.S):
    name = m.group(1)
    body = m.group(2)
    colnames = re.findall(r"^\s*`(\w+)`", body, re.M)
    tables[name] = colnames

# --- count rows per table by summing tuples in its INSERTs ---
def count_rows(tbl):
    total = 0
    for m in re.finditer(r"INSERT INTO `" + re.escape(tbl) + r"` (?:\([^)]*\)\s*)?VALUES (.*?);\s*\n", sql, re.S):
        block = m.group(1)
        # count top-level (...) tuples
        depth = 0; q = False; tuples = 0; started = False
        i = 0
        while i < len(block):
            c = block[i]
            if c == "'" and (i == 0 or block[i-1] != '\\'):
                q = not q
            elif not q:
                if c == '(':
                    if depth == 0:
                        tuples += 1
                    depth += 1
                elif c == ')':
                    depth -= 1
            i += 1
        total += tuples
    return total

print(f"{'TABLE':<28} {'ROWS':>7}  COLUMNS")
print("-" * 100)
rows_by_table = {}
for name in sorted(tables):
    n = count_rows(name)
    rows_by_table[name] = n
    colstr = ", ".join(tables[name])
    if len(colstr) > 66:
        colstr = colstr[:63] + "..."
    print(f"{name:<28} {n:>7}  {colstr}")
print("\nTOTAL tables:", len(tables))
