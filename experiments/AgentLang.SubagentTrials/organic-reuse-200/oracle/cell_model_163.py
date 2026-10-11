"""Independent bounded-cell specification, not a participant implementation.

Time coordinates are integer cells, mapped to UTC instants by runtime adapters.
This model deliberately does not call either domain implementation. Runtime
controls still have to establish that the adapters/scorers exercise the contract.
"""
from copy import deepcopy
import json
from pathlib import Path


def subscription(identifier="old", customer="alice", product="basic", start=0,
                 finish=4, status="active", cancelled=None):
    return dict(id=identifier, customer=customer, product=product, start=start,
                finish=finish, status=status, cancelled=cancelled, term="original")


def occupied(row):
    # Enumeration avoids copying the production interval-overlap comparison.
    cells = set(range(row["start"], row["finish"]))
    if row["status"] == "cancelled":
        cutoff = row["cancelled"]
        return set() if cutoff is None else {cell for cell in cells if cell < cutoff}
    return cells


def baseline():
    return dict(customers=["alice", "bob"], products=["basic", "other"],
                subscriptions=[subscription()], invoices=["invoice-marker"],
                payments=["payment-marker"], outbox=["outbox-marker"],
                sent=["sent-marker"])


def case(name, *, at=2, expiry=8, term="  annual  ", old_id="old", new_id="new",
         old=None, extras=()):
    store = baseline()
    if old is not None:
        store["subscriptions"][0] = old
    store["subscriptions"].extend(deepcopy(list(extras)))
    return dict(id=name, store=store,
                request=dict(oldId=old_id, newId=new_id, term=term, at=at, expiry=expiry))


CASES = [
    case("mid-period-boundary"),
    case("at-original-start", at=0),
    case("at-original-expiry", at=4),
    case("after-original-expiry", at=5),
    case("third-party-overlap", extras=[subscription("third", start=6, finish=10)]),
    case("touching-future-boundary", extras=[subscription("third", start=8, finish=10)]),
    case("touching-past-boundary", old=subscription(start=2, finish=4),
         extras=[subscription("third", start=0, finish=2)]),
    case("other-customer-not-conflict", extras=[subscription("third", customer="bob", start=6, finish=10)]),
    case("other-product-not-conflict", extras=[subscription("third", product="other", start=6, finish=10)]),
    case("cancelled-third-ends-at-boundary", old=subscription(start=2, finish=4),
         extras=[subscription("third", start=0, finish=10, status="cancelled", cancelled=2)]),
    case("empty-third-occupancy", extras=[subscription("third", start=6, finish=10,
                                                    status="cancelled", cancelled=6)]),
    case("replacement-id-is-old", new_id="old"),
    case("replacement-id-exists", new_id="third", extras=[subscription("third", product="other")]),
    case("blank-term", term=" \t "),
    case("empty-replacement-period", expiry=2),
    case("reversed-replacement-period", expiry=1),
    case("missing-old", old_id="missing"),
    case("already-cancelled-old", old=subscription(status="cancelled", cancelled=1)),
    case("already-cancelled-before-start", at=-1,
         old=subscription(status="cancelled", cancelled=1)),
    case("before-original-start", old=subscription(start=3, finish=5)),
    case("missing-old-precedes-duplicate", old_id="missing", new_id="old", term=""),
    case("cancelled-old-precedes-duplicate", new_id="old", term="",
         old=subscription(status="cancelled", cancelled=1)),
    case("before-start-precedes-duplicate", new_id="old", old=subscription(start=3, finish=5)),
    case("duplicate-precedes-blank-term", new_id="old", term=""),
    case("blank-term-precedes-bad-period", term="", expiry=2),
    case("bad-period-precedes-overlap", expiry=2, extras=[subscription("third", start=6, finish=10)]),
    case("old-customer-missing", old=subscription(customer="ghost")),
    case("old-product-missing", old=subscription(product="ghost")),
    case("old-customer-and-product-missing", old=subscription(customer="ghost", product="ghost-product")),
    case("blank-term-precedes-missing-customer", term="", old=subscription(customer="ghost")),
    case("bad-period-precedes-missing-customer", expiry=2, old=subscription(customer="ghost")),
    case("missing-product-precedes-overlap",
         old=subscription(product="ghost-product"),
         extras=[subscription("third", product="ghost-product", start=6, finish=10)]),
    case("duplicate-precedes-missing-customer", new_id="old", term="",
         old=subscription(customer="ghost")),
]

ADVERSARIAL_ORPHAN_REFERENCE_CASES = {
    "old-customer-missing",
    "old-product-missing",
    "old-customer-and-product-missing",
    "blank-term-precedes-missing-customer",
    "bad-period-precedes-missing-customer",
    "missing-product-precedes-overlap",
    "duplicate-precedes-missing-customer",
}


def expected(item, mutation=None):
    original = deepcopy(item["store"])
    req = item["request"]
    error = lambda code: dict(tag="error", code=code, input=original)
    if mutation == "duplicate-before-cancel" and any(
            row["id"] == req["newId"] for row in original["subscriptions"]):
        return error("DUPLICATE_SUBSCRIPTION")
    old = next((row for row in original["subscriptions"] if row["id"] == req["oldId"]), None)
    if old is None:
        return error("SUBSCRIPTION_NOT_FOUND")
    if old["status"] == "cancelled":
        return error("SUBSCRIPTION_ALREADY_CANCELLED")
    if req["at"] < old["start"]:
        return error("CANCEL_BEFORE_START")

    updated = deepcopy(original)
    ended = next(row for row in updated["subscriptions"] if row["id"] == req["oldId"])
    ended.update(status="cancelled", cancelled=req["at"])

    def start_error(code):
        if mutation == "leak-intermediate":
            return dict(tag="ok", store=updated, input=original)
        return error(code)

    if any(row["id"] == req["newId"] for row in original["subscriptions"]):
        return start_error("DUPLICATE_SUBSCRIPTION")
    if not req["term"].strip():
        return start_error("INVALID_SUBSCRIPTION_TERM")
    if req["expiry"] <= req["at"]:
        return start_error("SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START")
    if old["customer"] not in original["customers"]:
        return start_error("CUSTOMER_NOT_FOUND")
    if old["product"] not in original["products"]:
        return start_error("PRODUCT_NOT_FOUND")

    proposed = set(range(req["at"], req["expiry"]))
    examined = original if mutation == "start-before-cancel" else updated
    for row in examined["subscriptions"]:
        same_customer = row["customer"] == old["customer"]
        same_product = row["product"] == old["product"]
        same_scope = (same_customer if mutation == "customer-only" else
                      same_product if mutation == "product-only" else
                      same_customer and same_product)
        if mutation != "ignore-overlap" and same_scope and proposed & occupied(row):
            return start_error("SUBSCRIPTION_OVERLAP")
    created = subscription(req["newId"], old["customer"], old["product"],
                           req["at"], req["expiry"])
    created["term"] = req["term"].strip()
    updated["subscriptions"].append(created)
    return dict(tag="ok", store=updated, input=original)


def export():
    output = Path(__file__).resolve().parent
    rows = [dict(**item, expected=expected(item)) for item in CASES]
    controls = {}
    for mutation in ["duplicate-before-cancel", "leak-intermediate",
                     "start-before-cancel", "ignore-overlap", "customer-only", "product-only"]:
        rejected = [item["id"] for item in CASES if expected(item, mutation) != expected(item)]
        assert rejected, f"Matrix cannot distinguish {mutation}"
        controls[mutation] = rejected
    assert len({item["id"] for item in CASES}) == len(CASES)
    # No base case relies on overlapping same-owner active subscriptions.
    for item in CASES:
        before = deepcopy(item)
        expected(item)
        assert item == before, item["id"]
    (output / "cases.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    fixture_populations = {
        "public-valid-reference": [row["id"] for row in rows
                                   if row["id"] not in ADVERSARIAL_ORPHAN_REFERENCE_CASES],
        "adversarial-orphan-reference": [row["id"] for row in rows
                                         if row["id"] in ADVERSARIAL_ORPHAN_REFERENCE_CASES],
    }
    summary = dict(status="model-only; runtime control evidence recorded separately", cases=len(rows),
                   expectedSuccesses=sum(row["expected"]["tag"] == "ok" for row in rows),
                   fixturePopulations={key: {"count": len(ids), "caseIds": ids}
                                       for key, ids in fixture_populations.items()},
                   mutationDiscrimination=controls)
    (output / "model-checks.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    export()
