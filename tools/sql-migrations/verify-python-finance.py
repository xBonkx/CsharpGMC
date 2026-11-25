"""Run the original Python finance models on temporary synthetic SQLite data.
No live SQL connection, no GUI, no persistent user data or automatic git checkout.
Usage (from CsharpGMC root):
    python tools/sql-migrations/verify-python-finance.py C:\work\gmcsmeta
Use a Python environment with the pinned source's dependencies installed.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from datetime import date

PIN = "aa6b34dcf0314df93086a297f01d549ff58cf705"
ROOT = Path(__file__).resolve().parents[2]


def git(repo, *args):
    return subprocess.check_output(["git", "-C", str(repo), *args], text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("python_repo", type=Path)
    args = parser.parse_args()
    source = args.python_repo.resolve()
    if git(source, "rev-parse", "HEAD") != PIN or git(source, "status", "--porcelain"):
        raise SystemExit("Use a clean Python checkout at " + PIN)

    with tempfile.TemporaryDirectory(prefix="gmc-python-finance-") as temp:
        folder = Path(temp)
        for key in list(os.environ):
            if key.startswith("GMC_SMETA_"):
                del os.environ[key]
        for key in ("APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME"):
            os.environ[key] = str(folder / key)
        sys.path.insert(0, str(source))
        from smeta import db
        from smeta import acts as A
        from smeta.services import update_contract
        if not Path(db.__file__).resolve().is_relative_to(source):
            raise RuntimeError("Wrong Python module loaded")
        db_path = folder / "synthetic.db"
        session = db.open_db(str(db_path))
        engine = session.kw["bind"]
        if engine.dialect.name != "sqlite" or Path(engine.url.database).resolve() != db_path:
            raise RuntimeError("Only isolated SQLite is allowed")
        cases = []

        def contract(s, name, price=100, qty=10, mode="sum", form="", smr=0, transport=0, pnr=0):
            c = db.Contract(obj=db.Obj(name="Synthetic " + name), kind="main",
                            name=name, vat_rate=22, form=form, smr_mode=mode,
                            smr_amount=smr, transport_amount=transport, pnr_amount=pnr)
            p = db.Position(system=db.System(contract=c, name="P1"), name="Material",
                            unit="m", qty=qty, price=price)
            s.add_all([c, p])
            s.flush()
            return c, p

        def act(s, c, p, qty):
            a = A.create_act(s, c)
            a.date = date(2026, 11, 25)
            A.set_qty(s, a, p, qty)
            s.flush()
            return a

        def record(name, c, a):
            cases.append(dict(
                name=name,
                vatRate=c.vat_rate, vatTop=c.vat_top,
                contractMaterials=c.material_sum(), contractSmr=c.smr(),
                contractTransport=c.transport_amount or 0, contractPnr=c.pnr_amount or 0,
                contractPercent=c.discount_percent or 0, contractGross=c.total(),
                contractDiscount=c.discount_amount(),
                materials=a.materials(), smr=a.smr_amount or 0,
                transport=a.transport_amount or 0, pnr=a.pnr_amount or 0,
                percent=a.discount_percent or 0, manual=a.manual_total_cents,
                adjustment=a.gross_adjustment_cents or 0, gross=a.total(),
                vat=a.vat(), discount=a.discount_amount(),
                ks3Gross=A.ks3_figures(a)["total"],
                signed=bool(a.signed),
            ))

        with session() as s:
            c, p = contract(s, "inherited")
            old = act(s, c, p, 2)
            old.signed = True
            draft = act(s, c, p, 3)
            update_contract(s, c, discount_percent=5)
            s.flush()
            assert old.total() == 200 and draft.total() == 285
            record("signed-history", c, old)
            record("inherited", c, draft)
            c, p = contract(s, "fixed", price=1882837.04, qty=2)
            update_contract(s, c, discount_percent=3.543436268)
            a = act(s, c, p, 1)
            A.set_act_discount(a, "total", 1781970.78)
            update_contract(s, c, discount_percent=10)
            record("fixed", c, a)
            c, p = contract(s, "override")
            a = act(s, c, p, 2)
            A.set_act_discount(a, "percent", 0)
            update_contract(s, c, discount_percent=15)
            record("zero-override", c, a)
            A.set_act_discount(a, "percent", 25)
            record("individual-rate", c, a)
            A.set_act_discount(a, "total", 123.45)
            A.set_qty(s, a, p, 1)
            record("fixed-surcharge", c, a)
            c, p = contract(s, "sections", price=1237854, qty=1, mode="systems", transport=310000)
            p.system.smr_amount = 498000
            second = db.System(contract=c, name="P2", smr_amount=69000)
            s.add(second)
            s.flush()
            update_contract(s, c, discount_percent=5)
            a = act(s, c, p, 1)
            A.save_section_amounts(a, {p.system_id: 498000, second.id: 69000},
                                   {p.system_id: 50000, second.id: 10000})
            record("manual-sections", c, a)
            # DNS: keep separate VAT-on-top and adjustment values.
            c, p = contract(s, "dns", price=100, qty=10, form="dns", smr=200, transport=30, pnr=20)
            a = act(s, c, p, 10)
            a.smr_amount, a.transport_amount, a.pnr_amount = 200, 30, 20
            a.gross_adjustment_cents = 5
            record("vat-top", c, a)
            s.rollback()
        engine.dispose()
        output = folder / "oracle.json"
        output.write_text(json.dumps({"source": PIN, "cases": cases}, allow_nan=False), encoding="utf-8")
        env = os.environ.copy()
        env["GMC_SMETA_PYTHON_FINANCE_ORACLE"] = str(output)
        print("Python oracle:", PIN, "cases:", len(cases), flush=True)
        subprocess.run(["dotnet", "test", str(ROOT / "tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj"),
                        "-c", "Release", "--filter", "FullyQualifiedName~PythonFinanceOracleTests"],
                       cwd=ROOT, env=env, check=True)


if __name__ == "__main__":
    main()
