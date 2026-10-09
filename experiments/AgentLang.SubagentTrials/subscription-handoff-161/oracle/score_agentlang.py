"""Execute the handoff controls in the current AgentLang Release runtime.

The model cases remain outside the copied control project.  This adapter builds
typed AgentLang Stores from those cases, invokes the submitted Flow/2 words, and
compares complete canonical projections of both the input and returned Store.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
from time import strftime, gmtime


REPO = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
CONTROL_ROOT = REPO / ".agentlang" / "subscription-handoff-163" / "control"
SEED = REPO / ".agentlang" / "subscription-handoff-163" / "seeds" / "agentlang-project"
CLI = REPO / "src" / "AgentLang.Cli" / "bin" / "Release" / "net9.0" / "AgentLang.Cli.dll"
COMPOSED_SOURCE = CONTROL_ROOT / "agentlang-composed.agent"
ALL_SOURCE = CONTROL_ROOT / "agentlang-all-controls.agent"

ARMS = (
    "composed",
    "duplicate-before-cancel",
    "leak-intermediate",
    "start-before-cancel",
    "ignore-overlap",
    "customer-only",
    "product-only",
)

CUSTOMER_IDS = {
    "alice": "10000000-0000-0000-0000-000000000001",
    "bob": "10000000-0000-0000-0000-000000000002",
    "ghost": "10000000-0000-0000-0000-000000000003",
}
PRODUCT_IDS = {
    "basic": "20000000-0000-0000-0000-000000000001",
    "other": "20000000-0000-0000-0000-000000000002",
    "ghost": "20000000-0000-0000-0000-000000000003",
    "ghost-product": "20000000-0000-0000-0000-000000000003",
}
SUBSCRIPTION_IDS = {
    "old": "30000000-0000-0000-0000-000000000001",
    "new": "30000000-0000-0000-0000-000000000002",
    "third": "30000000-0000-0000-0000-000000000003",
    "missing": "30000000-0000-0000-0000-000000000004",
}
INVOICE_ID = "41000000-0000-0000-0000-000000000001"
PAYMENT_ID = "42000000-0000-0000-0000-000000000001"
BASE_TIME = datetime(2026, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
ADVERSARIAL_ORPHAN_REFERENCE_CASES = {
    "old-customer-missing",
    "old-product-missing",
    "old-customer-and-product-missing",
    "blank-term-precedes-missing-customer",
    "bad-period-precedes-missing-customer",
    "missing-product-precedes-overlap",
    "duplicate-precedes-missing-customer",
}


def iso_day(day: int) -> str:
    value = BASE_TIME + timedelta(days=day)
    return value.strftime("%Y-%m-%dT%H:%M:%S.%f") + "0+00:00"


def flow_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def id_for(mapping: dict[str, str], value: str) -> str:
    try:
        return mapping[value]
    except KeyError as error:
        raise ValueError(f"No deterministic typed ID mapping for {value!r}") from error


def list_expr(type_name: str, expressions: list[str]) -> str:
    if not expressions:
        return f"list.empty<{type_name}>()"
    result = f"list.singleton<{type_name}>({expressions[0]})"
    for expression in expressions[1:]:
        result = f"list.append({result}, {expression})"
    return result


def customer_expr(name: str) -> str:
    details = {
        "alice": ("alice@example.test", "regular", 1250),
        "bob": ("bob@example.test", "regular", 250),
    }
    email, kind, balance = details[name]
    return (
        "customer.new("
        f"id = CustomerId.new({flow_string(id_for(CUSTOMER_IDS, name))}), "
        f"email = Email.new({flow_string(email)}), kind = {flow_string(kind)}, "
        f"balance = Money.new({balance}), "
        f"created-at = Instant.new({flow_string(iso_day(0))}))"
    )


def product_expr(name: str) -> str:
    details = {"basic": ("Basic plan", 199), "other": ("Other plan", 299)}
    title, price = details[name]
    return (
        "product.new("
        f"id = ProductId.new({flow_string(id_for(PRODUCT_IDS, name))}), "
        f"name = {flow_string(title)}, unit-price = Money.new({price}))"
    )


def subscription_expr(row: dict) -> str:
    cancelled = (
        "option.none<Instant>()"
        if row["cancelled"] is None
        else f"option.some<Instant>(Instant.new({flow_string(iso_day(row['cancelled']))}))"
    )
    return (
        "subscription.new("
        f"id = SubscriptionId.new({flow_string(id_for(SUBSCRIPTION_IDS, row['id']))}), "
        f"customer-id = CustomerId.new({flow_string(id_for(CUSTOMER_IDS, row['customer']))}), "
        f"product-id = ProductId.new({flow_string(id_for(PRODUCT_IDS, row['product']))}), "
        f"term = {flow_string(row['term'])}, "
        f"started-at = Instant.new({flow_string(iso_day(row['start']))}), "
        f"expires-at = Instant.new({flow_string(iso_day(row['finish']))}), "
        f"status = SubscriptionStatus.new({flow_string(row['status'])}), "
        f"cancelled-at = {cancelled})"
    )


def invoice_expr() -> str:
    return (
        "invoice.new("
        f"id = InvoiceId.new({flow_string(INVOICE_ID)}), "
        f"customer-id = CustomerId.new({flow_string(CUSTOMER_IDS['alice'])}), "
        "lines = list.empty<InvoiceLine>(), total = Money.new(1), "
        f"created-at = Instant.new({flow_string(iso_day(0))}), "
        "status = InvoiceStatus.new(\"open\"))"
    )


def payment_expr() -> str:
    return (
        "payment.new("
        f"id = PaymentId.new({flow_string(PAYMENT_ID)}), "
        f"invoice-id = InvoiceId.new({flow_string(INVOICE_ID)}), "
        "amount = Money.new(1), provider-reference = \"payment-marker\", "
        f"paid-at = Instant.new({flow_string(iso_day(0))}))"
    )


def email_expr(kind: str) -> str:
    if kind == "outbox":
        address, subject, body = "outbox@example.test", "outbox-marker", "pending marker"
    else:
        address, subject, body = "sent@example.test", "sent-marker", "sent marker"
    return (
        "emailMessage.new("
        f"to = Email.new({flow_string(address)}), subject = {flow_string(subject)}, "
        f"body = {flow_string(body)})"
    )


def store_expr(store: dict) -> str:
    customers = list_expr("Customer", [customer_expr(name) for name in store["customers"]])
    products = list_expr("Product", [product_expr(name) for name in store["products"]])
    subscriptions = list_expr("Subscription", [subscription_expr(row) for row in store["subscriptions"]])
    invoices = list_expr("Invoice", [invoice_expr() for _ in store["invoices"]])
    payments = list_expr("Payment", [payment_expr() for _ in store["payments"]])
    outbox = list_expr("EmailMessage", [email_expr("outbox") for _ in store["outbox"]])
    sent = list_expr("EmailMessage", [email_expr("sent") for _ in store["sent"]])
    return (
        "store.new("
        f"customers = {customers}, products = {products}, subscriptions = {subscriptions}, "
        f"invoices = {invoices}, payments = {payments}, email-outbox = {outbox}, sent-emails = {sent})"
    )


def observation_expr(item: dict, arm: str, observer_name: str | None = None) -> str:
    request = item["request"]
    fn_name = observer_name or ("handoff.control.observe" if arm == "composed" else f"handoff.control.observe.{arm}")
    return (
        f"{fn_name}({store_expr(item['store'])}, "
        f"SubscriptionId.new({flow_string(id_for(SUBSCRIPTION_IDS, request['oldId']))}), "
        f"SubscriptionId.new({flow_string(id_for(SUBSCRIPTION_IDS, request['newId']))}), "
        f"{flow_string(request['term'])}, "
        f"Instant.new({flow_string(iso_day(request['at']))}), "
        f"Instant.new({flow_string(iso_day(request['expiry']))}))"
    )


MUTANT_SOURCE = r'''
record HandoffScopeState {
    field customer-id: CustomerId
    field product-id: ProductId
    field period: OccupiedPeriod
    field scope: String
    field overlaps: Bool
}

fn handoff.control.scope-step(state: HandoffScopeState, candidate: Subscription) -> HandoffScopeState {
    if state.overlaps {
        state
    } else {
        let in-scope = if state.scope == "customer" {
            candidate.customer-id == state.customer-id
        } else {
            candidate.product-id == state.product-id
        }
        if in-scope {
            match subscription.occupied-period(candidate) {
                some existing-period => handoffScopeState.new(
                    customer-id = state.customer-id,
                    product-id = state.product-id,
                    period = state.period,
                    scope = state.scope,
                    overlaps = occupied-period.overlaps?(state.period, existing-period))
                none => state
            }
        } else {
            state
        }
    }
}

fn handoff.control.overlap-in-scope?(subscriptions: List<Subscription>, customer-id: CustomerId, product-id: ProductId, handoff-at: Instant, replacement-expiry: Instant, scope: String) -> Bool {
    let period = occupiedPeriod.new(start = handoff-at, finish = replacement-expiry)
    let initial = handoffScopeState.new(
        customer-id = customer-id,
        product-id = product-id,
        period = period,
        scope = scope,
        overlaps = false)
    subscriptions.fold(initial, handoff.control.scope-step).overlaps
}

fn handoff.control.merge-created(cancelled-store: Store, started-store: Store, new-id: SubscriptionId) -> Result<Store, BusinessError> {
    match .store.subscription(new-id, started-store) {
        some created => result.ok<Store, BusinessError>(
            .store.with-subscriptions(cancelled-store, list.append(cancelled-store.subscriptions, created)))
        none => result.error<Store, BusinessError>(businessError.new(
            code = "SUBSCRIPTION_NOT_FOUND",
            message = "The created subscription is missing from the provisional Store."))
    }
}

fn handoff.control.start-with-mutant-scope(cancelled-store: Store, old: Subscription, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant, scope: String) -> Result<Store, BusinessError> {
    match .store.subscription(new-id, cancelled-store) {
        some existing => result.error<Store, BusinessError>(businessError.new(
            code = "DUPLICATE_SUBSCRIPTION",
            message = "A subscription with this identifier already exists."))
        none => {
            let no-subscriptions = .store.with-subscriptions(cancelled-store, list.empty<Subscription>())
            match subscription.start(no-subscriptions, new-id, old.customer-id, old.product-id, term, handoff-at, replacement-expiry) {
                error problem => result.error<Store, BusinessError>(problem)
                ok started-store => {
                    let overlaps = if scope == "ignore" {
                        false
                    } else {
                        handoff.control.overlap-in-scope?(cancelled-store.subscriptions, old.customer-id, old.product-id, handoff-at, replacement-expiry, scope)
                    }
                    if overlaps {
                        result.error<Store, BusinessError>(businessError.new(
                            code = "SUBSCRIPTION_OVERLAP",
                            message = "This customer already has an overlapping subscription for the selected scope."))
                    } else {
                        handoff.control.merge-created(cancelled-store, started-store, new-id)
                    }
                }
            }
        }
    }
}

fn handoff.control.duplicate-before-cancel(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> Result<Store, BusinessError> {
    match .store.subscription(new-id, store) {
        some existing => result.error<Store, BusinessError>(businessError.new(
            code = "DUPLICATE_SUBSCRIPTION",
            message = "A subscription with this identifier already exists."))
        none => subscription.handoff(store, old-id, new-id, term, handoff-at, replacement-expiry)
    }
}

fn handoff.control.leak-intermediate(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> Result<Store, BusinessError> {
    match .store.subscription(old-id, store) {
        none => subscription.cancel(store, old-id, handoff-at)
        some old => {
            match subscription.cancel(store, old-id, handoff-at) {
                error problem => result.error<Store, BusinessError>(problem)
                ok cancelled-store => {
                    match subscription.start(cancelled-store, new-id, old.customer-id, old.product-id, term, handoff-at, replacement-expiry) {
                        ok updated => result.ok<Store, BusinessError>(updated)
                        error problem => result.ok<Store, BusinessError>(cancelled-store)
                    }
                }
            }
        }
    }
}

fn handoff.control.start-before-cancel(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> Result<Store, BusinessError> {
    match .store.subscription(old-id, store) {
        none => subscription.cancel(store, old-id, handoff-at)
        some old => {
            match subscription.start(store, new-id, old.customer-id, old.product-id, term, handoff-at, replacement-expiry) {
                error problem => result.error<Store, BusinessError>(problem)
                ok started-store => subscription.cancel(started-store, old-id, handoff-at)
            }
        }
    }
}

fn handoff.control.ignore-overlap(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> Result<Store, BusinessError> {
    match .store.subscription(old-id, store) {
        none => subscription.cancel(store, old-id, handoff-at)
        some old => {
            match subscription.cancel(store, old-id, handoff-at) {
                error problem => result.error<Store, BusinessError>(problem)
                ok cancelled-store => handoff.control.start-with-mutant-scope(cancelled-store, old, new-id, term, handoff-at, replacement-expiry, "ignore")
            }
        }
    }
}

fn handoff.control.customer-only(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> Result<Store, BusinessError> {
    match .store.subscription(old-id, store) {
        none => subscription.cancel(store, old-id, handoff-at)
        some old => {
            match subscription.cancel(store, old-id, handoff-at) {
                error problem => result.error<Store, BusinessError>(problem)
                ok cancelled-store => handoff.control.start-with-mutant-scope(cancelled-store, old, new-id, term, handoff-at, replacement-expiry, "customer")
            }
        }
    }
}

fn handoff.control.product-only(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> Result<Store, BusinessError> {
    match .store.subscription(old-id, store) {
        none => subscription.cancel(store, old-id, handoff-at)
        some old => {
            match subscription.cancel(store, old-id, handoff-at) {
                error problem => result.error<Store, BusinessError>(problem)
                ok cancelled-store => handoff.control.start-with-mutant-scope(cancelled-store, old, new-id, term, handoff-at, replacement-expiry, "product")
            }
        }
    }
}

fn handoff.control.observe.duplicate-before-cancel(input: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> HandoffControlObservation {
    handoffControlObservation.new(input = input, outcome = handoff.control.duplicate-before-cancel(input, old-id, new-id, term, handoff-at, replacement-expiry))
}
fn handoff.control.observe.leak-intermediate(input: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> HandoffControlObservation {
    handoffControlObservation.new(input = input, outcome = handoff.control.leak-intermediate(input, old-id, new-id, term, handoff-at, replacement-expiry))
}
fn handoff.control.observe.start-before-cancel(input: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> HandoffControlObservation {
    handoffControlObservation.new(input = input, outcome = handoff.control.start-before-cancel(input, old-id, new-id, term, handoff-at, replacement-expiry))
}
fn handoff.control.observe.ignore-overlap(input: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> HandoffControlObservation {
    handoffControlObservation.new(input = input, outcome = handoff.control.ignore-overlap(input, old-id, new-id, term, handoff-at, replacement-expiry))
}
fn handoff.control.observe.customer-only(input: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> HandoffControlObservation {
    handoffControlObservation.new(input = input, outcome = handoff.control.customer-only(input, old-id, new-id, term, handoff-at, replacement-expiry))
}
fn handoff.control.observe.product-only(input: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> HandoffControlObservation {
    handoffControlObservation.new(input = input, outcome = handoff.control.product-only(input, old-id, new-id, term, handoff-at, replacement-expiry))
}
'''


def unwrap_structured(node):
    kind = node.get("kind")
    if kind == "record":
        return {field["name"]: unwrap_structured(field["value"]) for field in node["fields"]}
    if kind == "scalar":
        return unwrap_structured(node["value"])
    if kind == "list":
        return [unwrap_structured(item) for item in node.get("items", [])]
    if kind == "option":
        if node.get("case") == "none":
            return None
        return unwrap_structured(node["value"])
    if kind == "result":
        result = {"tag": node["case"]}
        payload = node.get("value", node.get("error"))
        if payload is not None:
            result["value"] = unwrap_structured(payload)
        return result
    if kind in ("string", "bool"):
        return node.get("value")
    if kind == "int":
        return int(node["value"])
    raise ValueError(f"Unsupported structured AgentLang node: {node!r}")


def normalized_projection(item: dict) -> dict:
    """Project model fixtures into the canonical typed AgentLang entity shape."""
    model_store = item["store"]
    customers = [
        {
            "id": CUSTOMER_IDS[name],
            "email": f"{name}@example.test",
            "kind": "regular",
            "balance": 1250 if name == "alice" else 250,
            "created-at": iso_day(0),
        }
        for name in model_store["customers"]
    ]
    products = [
        {
            "id": PRODUCT_IDS[name],
            "name": "Basic plan" if name == "basic" else "Other plan",
            "unit-price": 199 if name == "basic" else 299,
        }
        for name in model_store["products"]
    ]
    subscriptions = [
        {
            "id": SUBSCRIPTION_IDS[row["id"]],
            "customer-id": CUSTOMER_IDS[row["customer"]],
            "product-id": PRODUCT_IDS[row["product"]],
            "term": row["term"],
            "started-at": iso_day(row["start"]),
            "expires-at": iso_day(row["finish"]),
            "status": row["status"],
            "cancelled-at": None if row["cancelled"] is None else iso_day(row["cancelled"]),
        }
        for row in model_store["subscriptions"]
    ]
    invoices = [
        {
            "id": INVOICE_ID,
            "customer-id": CUSTOMER_IDS["alice"],
            "lines": [],
            "total": 1,
            "created-at": iso_day(0),
            "status": "open",
        }
        for _ in model_store["invoices"]
    ]
    payments = [
        {
            "id": PAYMENT_ID,
            "invoice-id": INVOICE_ID,
            "amount": 1,
            "provider-reference": "payment-marker",
            "paid-at": iso_day(0),
        }
        for _ in model_store["payments"]
    ]
    outbox = [
        {"to": "outbox@example.test", "subject": "outbox-marker", "body": "pending marker"}
        for _ in model_store["outbox"]
    ]
    sent = [
        {"to": "sent@example.test", "subject": "sent-marker", "body": "sent marker"}
        for _ in model_store["sent"]
    ]
    return {
        "customers": customers,
        "products": products,
        "subscriptions": subscriptions,
        "invoices": invoices,
        "payments": payments,
        "email-outbox": outbox,
        "sent-emails": sent,
    }


def result_projection(observation: dict) -> dict:
    input_store = observation["input"]
    outcome = observation["outcome"]
    if outcome["tag"] == "ok":
        return {"tag": "ok", "store": outcome["value"], "input": input_store}
    error = outcome.get("value") or {}
    return {"tag": "error", "code": error.get("code"), "input": input_store}


def expected_projection(item: dict) -> dict:
    expected_model = item["expected"]
    expected_input = normalized_projection(item)
    if expected_model["tag"] == "ok":
        return {
            "tag": "ok",
            "store": normalized_projection({"store": expected_model["store"]}),
            "input": expected_input,
        }
    return {"tag": "error", "code": expected_model["code"], "input": expected_input}


def score_eval_response(item: dict, response: dict) -> dict:
    result = {"caseId": item["id"], "executed": False, "passed": False}
    if response.get("ok") and response.get("kind") == "eval":
        structured = response.get("data", {}).get("structuredStack")
        if structured and len(structured.get("values", [])) == 1:
            actual = result_projection(unwrap_structured(structured["values"][0]))
            expected = expected_projection(item)
            result.update(executed=True, passed=(actual == expected))
            if actual != expected:
                result["expected"] = expected
                result["actual"] = actual
        else:
            result["setupError"] = "eval response omitted one structured observation value"
    else:
        result["setupError"] = response.get("error", response.get("text", "missing eval response"))
    return result


def json_line(value: dict) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def case_fixture_populations(cases: list[dict]) -> dict:
    classes = ("public-valid-reference", "adversarial-orphan-reference")
    return {
        fixture_class: {
            "count": sum(
                (item["id"] in ADVERSARIAL_ORPHAN_REFERENCE_CASES)
                == (fixture_class == "adversarial-orphan-reference")
                for item in cases
            ),
            "caseIds": [
                item["id"] for item in cases
                if (item["id"] in ADVERSARIAL_ORPHAN_REFERENCE_CASES)
                == (fixture_class == "adversarial-orphan-reference")
            ],
        }
        for fixture_class in classes
    }


def source_for_run() -> str:
    positive = COMPOSED_SOURCE.read_text(encoding="utf-8-sig")
    return positive.rstrip() + "\n\n" + MUTANT_SOURCE.lstrip()


def run_cli(project: Path, requests: list[dict]) -> tuple[int, str, str, list[dict]]:
    request_text = "\n".join(json_line(item) for item in requests) + "\n"
    process = subprocess.run(
        ["dotnet", str(CLI), "--project", str(project), "--jsonl"],
        cwd=REPO,
        input=request_text,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=300,
    )
    rows = []
    for index, line in enumerate(process.stdout.splitlines(), 1):
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise RuntimeError(f"CLI stdout line {index} is not JSON: {line[:500]!r}") from error
    return process.returncode, process.stdout, process.stderr, rows


def next_run_dir(root: Path) -> Path:
    timestamp = strftime("%Y%m%dT%H%M%SZ", gmtime())
    run_root = root / "agentlang-runs"
    run_root.mkdir(parents=True, exist_ok=True)
    index = 1
    while True:
        candidate = run_root / f"{timestamp}-{index:02d}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            index += 1


def score_all() -> dict:
    cases = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    if not SEED.is_dir():
        raise FileNotFoundError(f"Refreshed AgentLang seed is missing: {SEED}")
    if not CLI.is_file():
        raise FileNotFoundError(f"Current Release CLI is missing: {CLI}")
    run_dir = next_run_dir(CONTROL_ROOT)
    project = run_dir / "agentlang-project"
    shutil.copytree(SEED, project)

    source = source_for_run()
    source_path = run_dir / "all-controls.agent"
    source_path.write_text(source, encoding="utf-8")
    ALL_SOURCE.write_text(source, encoding="utf-8")

    requests = [
        {"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": source},
        {"op": "test", "word": "subscription.handoff"},
        {"op": "commit", "word": "subscription.handoff", "library": True},
    ]
    scored_case_slots: list[tuple[str, dict]] = []
    for arm in ARMS:
        for item in cases:
            requests.append({
                "op": "eval",
                "frontend": "flow",
                "syntaxVersion": 2,
                "structured": True,
                "code": observation_expr(item, arm),
            })
            scored_case_slots.append((arm, item))

    exit_code, stdout, stderr, responses = run_cli(project, requests)
    (run_dir / "requests.jsonl").write_text(
        "\n".join(json_line(item) for item in requests) + "\n", encoding="utf-8"
    )
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")
    if len(responses) != len(requests):
        setup = {
            "passed": False,
            "exitCode": exit_code,
            "expectedResponseCount": len(requests),
            "actualResponseCount": len(responses),
            "firstResponses": responses[:4],
            "stderr": stderr,
        }
        report = {
            "status": "setup-failure",
            "runtime": str(CLI.relative_to(REPO)),
            "runDirectory": str(run_dir.relative_to(REPO)),
            "caseCount": len(cases),
            "setupFailure": setup,
            "arms": {arm: {"executedCaseCount": 0, "passedCaseCount": 0, "failingCaseIds": []} for arm in ARMS},
        }
        (run_dir / "score.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        return report

    qualification = {
        "define": responses[0],
        "tests": responses[1],
        "commit": responses[2],
    }
    setup_failed = any(not qualification[name].get("ok", False) for name in ("define", "tests", "commit"))
    arm_results = {arm: [] for arm in ARMS}
    eval_responses = responses[3:]
    for (arm, item), response in zip(scored_case_slots, eval_responses):
        case_result = {"caseId": item["id"], "executed": False, "passed": False}
        if response.get("ok") and response.get("kind") == "eval":
            structured = response.get("data", {}).get("structuredStack")
            if structured and len(structured.get("values", [])) == 1:
                observation = unwrap_structured(structured["values"][0])
                actual = result_projection(observation)
                expected_input = normalized_projection(item)
                expected_model = item["expected"]
                if expected_model["tag"] == "ok":
                    expected = {
                        "tag": "ok",
                        "store": normalized_projection({"store": expected_model["store"]}),
                        "input": expected_input,
                    }
                else:
                    expected = {
                        "tag": "error",
                        "code": expected_model["code"],
                        "input": expected_input,
                    }
                case_result.update(executed=True, passed=(actual == expected))
                if actual != expected:
                    case_result["expected"] = expected
                    case_result["actual"] = actual
            else:
                case_result["setupError"] = "eval response omitted one structured observation value"
        else:
            case_result["setupError"] = response.get("error", response.get("text", "missing eval response"))
        arm_results[arm].append(case_result)

    arms = {}
    for arm, results in arm_results.items():
        executed = sum(result["executed"] for result in results)
        failures = [result["caseId"] for result in results if result["executed"] and not result["passed"]]
        setup_errors = [result for result in results if not result["executed"]]
        arms[arm] = {
            "executedCaseCount": executed,
            "passedCaseCount": executed - len(failures),
            "failingCaseIds": failures,
            "setupErrorCount": len(setup_errors),
        }

    expected_count = len(cases)
    positive_ok = (
        not setup_failed
        and arms["composed"]["executedCaseCount"] == expected_count
        and arms["composed"]["passedCaseCount"] == expected_count
    )
    mutants_ok = all(
        arms[arm]["executedCaseCount"] == expected_count
        and bool(arms[arm]["failingCaseIds"])
        for arm in ARMS
        if arm != "composed"
    )
    report = {
        "status": "passed" if positive_ok and mutants_ok else "control-failure",
        "runtime": str(CLI.relative_to(REPO)),
        "runtimeSha256": hashlib.sha256(CLI.read_bytes()).hexdigest(),
        "seed": str(SEED.relative_to(REPO)),
        "caseSource": str((HERE / "cases.json").relative_to(REPO)),
        "validationCommand": "python experiments/AgentLang.SubagentTrials/subscription-handoff-161/oracle/score_agentlang.py",
        "runDirectory": str(run_dir.relative_to(REPO)),
        "caseCount": expected_count,
        "fixturePopulations": case_fixture_populations(cases),
        "expectedSuccesses": sum(item["expected"]["tag"] == "ok" for item in cases),
        "qualification": qualification,
        "setupFailure": None if not setup_failed else {
            "stage": next(name for name in ("define", "tests", "commit") if not qualification[name].get("ok", False)),
            "responses": qualification,
        },
        "arms": arms,
        "allExpectedMutantsExecutedAndRejected": mutants_ok,
        "caseResults": arm_results,
    }
    setup_attempts_path = CONTROL_ROOT / "evidence" / "agentlang-setup-attempts.json"
    if setup_attempts_path.is_file():
        report["preparationFailures"] = json.loads(setup_attempts_path.read_text(encoding="utf-8-sig"))
    (run_dir / "score.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


ACTOR_OBSERVER_SOURCE = r'''record HandoffActorScoringObservation {
    field input: Store
    field outcome: Result<Store, BusinessError>
}

fn handoff.control.scoring-observe(input: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, handoff-at: Instant, replacement-expiry: Instant) -> HandoffActorScoringObservation {
    handoffActorScoringObservation.new(input = input, outcome = subscription.handoff(input, old-id, new-id, term, handoff-at, replacement-expiry))
}
'''


def score_actor(actor_project: Path) -> dict:
    """Score an already-saved actor project without defining its target word."""
    cases = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    actor_project = actor_project if actor_project.is_absolute() else REPO / actor_project
    if not actor_project.is_dir():
        raise FileNotFoundError(f"Saved actor project is missing: {actor_project}")
    run_dir = next_run_dir(CONTROL_ROOT / "actor")
    project = run_dir / "actor-project"
    shutil.copytree(actor_project, project)

    wrapper_path = run_dir / "actor-observer-only.agent"
    wrapper_path.write_text(ACTOR_OBSERVER_SOURCE, encoding="utf-8")
    requests = [
        {"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": ACTOR_OBSERVER_SOURCE},
    ]
    for item in cases:
        requests.append({
            "op": "eval",
            "frontend": "flow",
            "syntaxVersion": 2,
            "structured": True,
            "code": observation_expr(item, "composed", "handoff.control.scoring-observe"),
        })

    exit_code, stdout, stderr, responses = run_cli(project, requests)
    (run_dir / "requests.jsonl").write_text(
        "\n".join(json_line(item) for item in requests) + "\n", encoding="utf-8"
    )
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")

    define_response = responses[0] if responses else {}
    defined_names = [
        word.get("name")
        for word in define_response.get("data", {}).get("words", [])
    ]
    target_redefined = "subscription.handoff" in defined_names
    setup_failure = None
    case_results = []
    if len(responses) != len(requests):
        setup_failure = {
            "stage": "response-count",
            "exitCode": exit_code,
            "expectedResponseCount": len(requests),
            "actualResponseCount": len(responses),
            "stderr": stderr,
        }
    elif not define_response.get("ok", False):
        setup_failure = {"stage": "observer-define", "response": define_response}
    elif target_redefined:
        setup_failure = {
            "stage": "observer-define",
            "message": "The wrapper-only request unexpectedly declared subscription.handoff.",
            "definedWordNames": defined_names,
        }
    else:
        case_results = [score_eval_response(item, response) for item, response in zip(cases, responses[1:])]

    executed = sum(result.get("executed", False) for result in case_results)
    failing = [result["caseId"] for result in case_results if result.get("executed") and not result.get("passed")]
    setup_errors = [result for result in case_results if not result.get("executed")]
    arm = {
        "executedCaseCount": executed,
        "passedCaseCount": executed - len(failing),
        "failingCaseIds": failing,
        "setupErrorCount": len(setup_errors),
    }
    report = {
        "status": "passed" if setup_failure is None and executed == len(cases) and not failing else "actor-score-failure",
        "mode": "wrapper-only saved actor score",
        "runtime": str(CLI.relative_to(REPO)),
        "runtimeSha256": hashlib.sha256(CLI.read_bytes()).hexdigest(),
        "actorProject": str(actor_project.relative_to(REPO)),
        "copiedScoringProject": str(project.relative_to(REPO)),
        "caseSource": str((HERE / "cases.json").relative_to(REPO)),
        "validationCommand": "python experiments/AgentLang.SubagentTrials/subscription-handoff-161/oracle/score_agentlang.py --actor-project <saved-project>",
        "runDirectory": str(run_dir.relative_to(REPO)),
        "caseCount": len(cases),
        "fixturePopulations": case_fixture_populations(cases),
        "definedWordNames": defined_names,
        "targetWordRedefined": target_redefined,
        "participantQualification": "Not assessed by this wrapper-only hidden acceptance run; record participant self-tests, library maturity, and commit status separately.",
        "setupFailure": setup_failure,
        "arms": {"composed": arm},
        "caseResults": case_results,
    }
    attempts_path = CONTROL_ROOT / "evidence" / "agentlang-setup-attempts.json"
    if attempts_path.is_file():
        report["preparationFailures"] = json.loads(attempts_path.read_text(encoding="utf-8-sig"))
    (run_dir / "score.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--actor-project", type=Path, help="Score an existing project without defining or committing its subscription.handoff word.")
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()
    report = score_actor(args.actor_project) if args.actor_project else score_all()
    summary_path = args.summary or (
        CONTROL_ROOT / "control-summary-agentlang-actor.json"
        if args.actor_project
        else CONTROL_ROOT / "control-summary-agentlang.json"
    )
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "caseResults"}, indent=2, ensure_ascii=False))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
