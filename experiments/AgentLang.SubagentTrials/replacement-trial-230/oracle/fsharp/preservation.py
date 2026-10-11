#!/usr/bin/env python3
"""Audit F# source preservation for replacement-trial-230 without compiling it.

CLI (from the repository root)::

    python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/fsharp/preservation.py \
        --candidate .agentlang/replacement-trial-230/actors/fsharp

The audit checks that only Operations.fs and SelfTests.fs have permitted
scoped changes, that protected seed files are byte-identical, that the
scenario callback adapts to RenewalTiming, and that one direct grouped-time
test checks the API result. It does not execute or compile candidate code;
behavioral field mapping is checked by the separate nine-case grader.
Whitespace and comments outside literals are ignored for source comparison;
quoted literals remain exact tokens so inherited assertion text cannot drift.
"""

from __future__ import annotations

import argparse
from collections import Counter
from collections.abc import Iterable
import hashlib
import json
from pathlib import Path
import re


HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[4]
DEFAULT_BASELINE = REPOSITORY / ".agentlang" / "replacement-trial-230" / "baselines" / "fsharp"
DEFAULT_CANDIDATE = REPOSITORY / ".agentlang" / "replacement-trial-230" / "actors" / "fsharp"
PROTECTED_FILES = (
    "Domain.fs",
    "Fixtures.fs",
    "Program.fs",
    "SubscriptionBatch.Conventional.fsproj",
)
REQUIRED_FILES = (*PROTECTED_FILES, "Operations.fs", "SelfTests.fs")
GENERATED_DIRS = {".git", "artifacts", "bin", "obj", "__pycache__"}
SOURCE_SUFFIXES = {".fs", ".fsi", ".fsx", ".fsproj", ".cs", ".csproj", ".vb", ".vbproj", ".props", ".targets", ".sln", ".slnx"}


class AuditError(ValueError):
    """A source-preservation condition failed."""


def sha256(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def is_identifier(token: str) -> bool:
    return bool(token) and (token[0].isalpha() or token[0] == "_") and all(
        character.isalnum() or character in "_'’" for character in token
    )


def _skip_quoted(source: str, start: int) -> int:
    """Return the first index after an ordinary or triple-quoted string."""
    if source.startswith('"""', start):
        end = source.find('"""', start + 3)
        if end < 0:
            raise AuditError("unterminated triple-quoted string")
        return end + 3
    index = start + 1
    while index < len(source):
        if source[index] == "\\":
            index += 2
            continue
        if source[index] == '"':
            return index + 1
        index += 1
    raise AuditError("unterminated string literal")


def tokenize(source: str) -> list[str]:
    """F#-sufficient lexical normalization; preserves every quoted literal."""
    tokens: list[str] = []
    index = 0
    operators = ("->", "|>", "||", "&&", "<=", ">=", "<>", "::", ":=", "..")
    while index < len(source):
        character = source[index]
        if character.isspace():
            index += 1
            continue
        if source.startswith("//", index):
            newline = source.find("\n", index + 2)
            index = len(source) if newline < 0 else newline + 1
            continue
        if source.startswith("(*", index):
            depth = 1
            index += 2
            while index < len(source) and depth:
                if source.startswith("(*", index):
                    depth += 1
                    index += 2
                elif source.startswith("*)", index):
                    depth -= 1
                    index += 2
                else:
                    index += 1
            if depth:
                raise AuditError("unterminated block comment")
            continue
        if character == '"':
            end = _skip_quoted(source, index)
            tokens.append(source[index:end])
            index = end
            continue
        if character == "@" and index + 1 < len(source) and source[index + 1] == '"':
            end = index + 2
            while end < len(source):
                if source.startswith('""', end):
                    end += 2
                elif source[end] == '"':
                    end += 1
                    break
                else:
                    end += 1
            if end > len(source) or source[end - 1] != '"':
                raise AuditError("unterminated verbatim string literal")
            tokens.append(source[index:end])
            index = end
            continue
        if character.isalpha() or character == "_":
            end = index + 1
            while end < len(source) and (source[end].isalnum() or source[end] in "_'"):
                end += 1
            tokens.append(source[index:end])
            index = end
            continue
        if character.isdigit():
            end = index + 1
            while end < len(source) and (source[end].isalnum() or source[end] in "_'"):
                end += 1
            tokens.append(source[index:end])
            index = end
            continue
        operator = next((item for item in operators if source.startswith(item, index)), None)
        if operator is not None:
            tokens.append(operator)
            index += len(operator)
            continue
        tokens.append(character)
        index += 1
    return tokens


def read_tokens(path: Path) -> list[str]:
    try:
        return tokenize(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, AuditError) as error:
        raise AuditError(f"cannot read F# source {path}: {error}") from error


def find_all(tokens: list[str], pattern: Iterable[str]) -> list[int]:
    pattern = list(pattern)
    if not pattern:
        return []
    return [index for index in range(len(tokens) - len(pattern) + 1) if tokens[index:index + len(pattern)] == pattern]


def find_one(tokens: list[str], pattern: Iterable[str], label: str) -> int:
    found = find_all(tokens, pattern)
    if len(found) != 1:
        raise AuditError(f"expected one {label}, found {len(found)}")
    return found[0]


def matching(tokens: list[str], start: int, opening: str = "(", closing: str = ")") -> int:
    if start >= len(tokens) or tokens[start] != opening:
        raise AuditError(f"expected {opening!r} at token {start}")
    depth = 0
    pairs = {"(": ")", "{": "}", "[": "]"}
    closers = {value: key for key, value in pairs.items()}
    for index in range(start, len(tokens)):
        item = tokens[index]
        if item in pairs:
            depth += 1
        elif item in closers:
            depth -= 1
            if depth == 0:
                if item != closing:
                    raise AuditError(f"mismatched delimiter at token {index}")
                return index
            if depth < 0:
                break
    raise AuditError(f"unclosed {opening!r} at token {start}")


def parse_simple_path(tokens: list[str], start: int) -> tuple[list[str], int]:
    """Read an identifier/member path, or one parenthesized argument."""
    if start >= len(tokens):
        raise AuditError("expected a simple argument")
    if tokens[start] == "(":
        end = matching(tokens, start)
        if end == start + 2 and is_identifier(tokens[start + 1]):
            return [tokens[start + 1]], end + 1
        inner = tokens[start + 1:end]
        if not inner:
            raise AuditError("empty argument parentheses")
        return inner, end + 1
    if not is_identifier(tokens[start]):
        raise AuditError(f"expected an identifier/member path, found {tokens[start]!r}")
    path = [tokens[start]]
    index = start + 1
    while index + 1 < len(tokens) and tokens[index] == "." and is_identifier(tokens[index + 1]):
        path.extend(tokens[index:index + 2])
        index += 2
    return path, index


def parse_timing_record(tokens: list[str], start: int) -> tuple[dict[str, list[str]], int]:
    if start >= len(tokens) or tokens[start] != "{":
        raise AuditError("expected a RenewalTiming record literal")
    end = matching(tokens, start, "{", "}")
    inside = tokens[start + 1:end]
    labels: list[tuple[str, int]] = []
    depth = 0
    index = 0
    while index < len(inside):
        item = inside[index]
        if depth == 0 and item in {"At", "ExpiresAt"} and inside[index + 1:index + 2] == ["="]:
            labels.append((item, index + 2))
            index += 2
            continue
        if item in ("(", "{", "["):
            depth += 1
        elif item in (")", "}", "]"):
            depth -= 1
        index += 1
    if len(labels) != 2 or {name for name, _ in labels} != {"At", "ExpiresAt"}:
        raise AuditError("RenewalTiming literal must contain exactly one At and one ExpiresAt field")
    fields: dict[str, list[str]] = {}
    for position, (name, value_start) in enumerate(labels):
        value_end = labels[position + 1][1] - 2 if position + 1 < len(labels) else len(inside)
        value = inside[value_start:value_end]
        while value and value[0] == ";":
            value = value[1:]
        while value and value[-1] == ";":
            value = value[:-1]
        if not value:
            raise AuditError(f"RenewalTiming.{name} has no value")
        fields[name] = value
    return fields, end + 1


def parse_timing_argument(tokens: list[str], start: int) -> tuple[dict[str, list[str]] | None, list[str] | None, int]:
    wrapped = tokens[start] == "(" if start < len(tokens) else False
    inner_start = start + 1 if wrapped else start
    if inner_start < len(tokens) and tokens[inner_start] == "{":
        record, end = parse_timing_record(tokens, inner_start)
        if wrapped:
            if end >= len(tokens) or tokens[end] != ")":
                raise AuditError("parenthesized RenewalTiming literal is not closed")
            end += 1
        return record, None, end
    path, end = parse_simple_path(tokens, inner_start)
    if wrapped:
        if end >= len(tokens) or tokens[end] != ")":
            raise AuditError("parenthesized RenewalTiming value is not closed")
        end += 1
    return None, path, end


def parse_record_type(tokens: list[str]) -> tuple[list[str], tuple[int, int]]:
    declarations = find_all(tokens, ["type", "RenewalTiming", "="])
    if len(declarations) != 1:
        raise AuditError(f"expected one RenewalTiming type declaration, found {len(declarations)}")
    start = declarations[0]
    open_index = start + 3
    while open_index < len(tokens) and tokens[open_index] != "{":
        open_index += 1
    end = matching(tokens, open_index, "{", "}")
    # This record has two fields whose canonical types are Instant. Optional
    # semicolons and line breaks are formatting, not semantic changes.
    fields = tokens[open_index + 1:end]
    fields = [item for item in fields if item != ";"]
    expected = ["At", ":", "Instant", "ExpiresAt", ":", "Instant"]
    if fields != expected:
        raise AuditError("RenewalTiming must declare At: Instant and ExpiresAt: Instant in that order")
    return tokens[start:end + 1], (start, end + 1)


def function_span(tokens: list[str], name: str, next_name: str) -> tuple[int, int]:
    starts = find_all(tokens, ["let", name])
    if len(starts) != 1:
        raise AuditError(f"expected one let {name}, found {len(starts)}")
    start = starts[0]
    ends = [index for index in find_all(tokens, ["let", next_name]) if index > start]
    if len(ends) != 1:
        raise AuditError(f"expected one following let {next_name} after {name}, found {len(ends)}")
    return start, ends[0]


def parse_typed_binders(tokens: list[str], start: int, count: int) -> tuple[list[tuple[str, list[str]]], int]:
    binders: list[tuple[str, list[str]]] = []
    index = start
    for _ in range(count):
        if index >= len(tokens) or tokens[index] != "(":
            raise AuditError("function signature is missing a typed parameter")
        end = matching(tokens, index)
        group = tokens[index + 1:end]
        if len(group) < 3 or not is_identifier(group[0]) or group[1] != ":":
            raise AuditError("function parameter must have a name and type annotation")
        binders.append((group[0], group[2:]))
        index = end + 1
    return binders, index


def _case_current(tokens: list[str], span: tuple[int, int]) -> str:
    start, end = span
    hits = []
    for index in range(start, end - 3):
        if tokens[index:index + 2] == ["|", "Ok"]:
            offset = index + 2
            if offset < end and is_identifier(tokens[offset]) and offset + 1 < end and tokens[offset + 1] == "->":
                hits.append(tokens[offset])
    if len(hits) != 1:
        raise AuditError(f"expected one Ok current-value branch in renewBatchStep, found {len(hits)}")
    return hits[0]


def monthly_batch_rhs_span(tokens: list[str], span: tuple[int, int]) -> tuple[int, int]:
    start, end = span
    monthly = find_all(tokens[start:end], ["|", "Monthly", "->"])
    annual = find_all(tokens[start:end], ["|", "Annual", "->"])
    if len(monthly) != 1 or len(annual) != 1 or monthly[0] >= annual[0]:
        raise AuditError("renewBatchStep must keep Monthly and Annual plan branches")
    rhs_start = start + monthly[0] + 3
    rhs_end = start + annual[0]
    return rhs_start, rhs_end


def normalize_step(tokens: list[str], span: tuple[int, int], baseline_names: tuple[str, str, str] | None = None) -> tuple[list[str], tuple[str, str, str], tuple[int, int]]:
    start, end = span
    cursor = start + 2
    binders, cursor = parse_typed_binders(tokens, cursor, 2)
    state, change = (name for name, _ in binders)
    current = _case_current(tokens, span)
    names = (state, change, current)
    if len(set(names)) != 3:
        raise AuditError("renewBatchStep parameter and current-value names must be distinct")
    rhs_start, rhs_end = monthly_batch_rhs_span(tokens, span)
    mapping = dict(zip(names, baseline_names or names))
    normalized = [mapping.get(token, token) for token in tokens[start:end]]
    normalized_rhs_start = rhs_start - start
    normalized_rhs_end = rhs_end - start
    normalized[normalized_rhs_start:normalized_rhs_end] = ["<monthly-renewal-call>"]
    return normalized, names, (rhs_start, rhs_end)


def replace_spans(tokens: list[str], spans: list[tuple[int, int, list[str]]]) -> list[str]:
    result = list(tokens)
    for start, end, replacement in sorted(spans, key=lambda item: item[0], reverse=True):
        result[start:end] = replacement
    return result


def operations_preserved(candidate: list[str], baseline: list[str]) -> None:
    _, type_span = parse_record_type(candidate)
    module = find_all(candidate, ["module", "Subscription"])
    if len(module) != 1 or type_span[0] > module[0]:
        raise AuditError("RenewalTiming must be declared before the Subscription module")
    c_monthly_span = function_span(candidate, "renewMonthly", "renewAnnual")
    b_monthly_span = function_span(baseline, "renewMonthly", "renewAnnual")
    c_step_span = function_span(candidate, "renewBatchStep", "renewBatch")
    b_step_span = function_span(baseline, "renewBatchStep", "renewBatch")
    b_step, b_names, _ = normalize_step(baseline, b_step_span)
    c_step, _, _ = normalize_step(candidate, c_step_span, b_names)
    if c_step != b_step:
        raise AuditError("renewBatchStep changed outside its Monthly renewal argument construction")
    candidate_normal = replace_spans(
        candidate,
        [
            (*type_span, []),
            (*c_monthly_span, ["<renewMonthly-definition>"]),
            (*c_step_span, ["<renewBatchStep-definition>"]),
        ],
    )
    baseline_normal = replace_spans(
        baseline,
        [
            (*b_monthly_span, ["<renewMonthly-definition>"]),
            (*b_step_span, ["<renewBatchStep-definition>"]),
        ],
    )
    if candidate_normal != baseline_normal:
        raise AuditError("Operations.fs changed outside RenewalTiming, renewMonthly, and the Monthly batch caller")


def runner_anchors(tokens: list[str]) -> tuple[int, int]:
    monthly = [index for index in range(len(tokens) - 1) if tokens[index:index + 2] == ["runRenewalScenarios", '"monthly"']]
    annual = [index for index in range(len(tokens) - 1) if tokens[index:index + 2] == ["runRenewalScenarios", '"annual"']]
    if len(monthly) != 1 or len(annual) != 1 or monthly[0] >= annual[0]:
        raise AuditError("SelfTests.fs must retain one monthly and one annual scenario runner")
    return monthly[0], annual[0]


def main_run_anchor(tokens: list[str]) -> int:
    """Locate the public SelfTests.run entry where the scenario calls live."""
    return find_one(tokens, ["let", "run", "(", ")", "="], "SelfTests.run entry")


def resolve_local_timing(
    tokens: list[str], name: str, before: int, at_name: str | None, expiry_name: str | None, label: str
) -> dict[str, list[str]]:
    declarations = find_all(tokens[:before], ["let", name])
    if not declarations:
        raise AuditError(f"{label} local RenewalTiming has no visible binding")
    declaration = declarations[-1]
    equal = next((index for index in range(declaration + 2, before) if tokens[index] == "="), None)
    if equal is None:
        raise AuditError(f"{label} local RenewalTiming binding has no value")
    record, _, _ = parse_timing_argument(tokens, equal + 1)
    expected = {"At": [at_name], "ExpiresAt": [expiry_name]} if at_name is not None and expiry_name is not None else None
    if record is None or (expected is not None and record != expected):
        raise AuditError(f"{label} local RenewalTiming must map its At and ExpiresAt inputs")
    return record


def parse_scenario_adapter(tokens: list[str], start: int, baseline: list[str]) -> tuple[int, int]:
    if tokens[start:start + 2] != ["runRenewalScenarios", '"monthly"']:
        raise AuditError("monthly scenario runner anchor is malformed")
    cursor = start + 2
    if cursor >= len(tokens) or tokens[cursor] != "(":
        raise AuditError("monthly scenario must adapt its callback with a local function")
    callback_end = matching(tokens, cursor)
    body = tokens[cursor + 1:callback_end]
    if not body or body[0] != "fun":
        raise AuditError("monthly scenario callback must construct RenewalTiming")
    parameter_names: list[str] = []
    body_cursor = 1
    while body_cursor < len(body) and body[body_cursor] != "->":
        if not is_identifier(body[body_cursor]):
            raise AuditError("monthly callback must bind five renewal inputs before its body")
        parameter_names.append(body[body_cursor])
        body_cursor += 1
    if len(parameter_names) != 5 or body_cursor >= len(body) or len(set(parameter_names)) != 5:
        raise AuditError("monthly callback must adapt the existing five-input scenario to RenewalTiming")
    store, old_id, new_id, at, expiry = parameter_names
    call_start = find_one(body, ["Subscription", ".", "renewMonthly"], "monthly callback API call")
    call = body[call_start:]
    call_cursor = 3
    args = []
    for _ in range(3):
        argument, call_cursor = parse_simple_path(call, call_cursor)
        args.append(argument)
    if args != [[store], [old_id], [new_id]]:
        raise AuditError("monthly scenario callback must forward its Store and IDs")
    record, timing_path, call_end = parse_timing_argument(call, call_cursor)
    if call_end != len(call):
        raise AuditError("monthly callback must return its direct renewMonthly call")
    if record is None:
        if timing_path is None or len(timing_path) != 1:
            raise AuditError("monthly scenario callback must construct a RenewalTiming value")
        record = resolve_local_timing(body, timing_path[0], call_start, at, expiry, "monthly callback")
    if record != {"At": [at], "ExpiresAt": [expiry]}:
        raise AuditError("monthly scenario callback must map its start and expiry inputs to the matching fields")
    # The old runner input literals and subscription IDs are inherited cases,
    # not part of the permitted callback adaptation.
    baseline_monthly, baseline_annual = runner_anchors(baseline)
    old_call = baseline[baseline_monthly:baseline_annual]
    old_prefix = ["runRenewalScenarios", '"monthly"', "Subscription", ".", "renewMonthly"]
    if old_call[:len(old_prefix)] != old_prefix:
        raise AuditError("baseline monthly scenario call does not match the expected seed")
    tail = old_call[len(old_prefix):]
    after_callback = callback_end + 1
    if tokens[after_callback:after_callback + len(tail)] != tail:
        raise AuditError("monthly scenario label, term, or seed IDs changed")
    return callback_end + 1 + len(tail), callback_end + 1


def find_api_calls(tokens: list[str]) -> list[int]:
    return find_all(tokens, ["Subscription", ".", "renewMonthly"])


def _has_assertion_use(tokens: list[str], start: int, end: int, result_name: str | None) -> tuple[bool, str]:
    if result_name is not None:
        uses = [index for index in range(start, end) if tokens[index] == result_name]
        for use in uses:
            window_start = max(start, use - 64)
            window_end = min(end, use + 96)
            context = tokens[window_start:window_end]
            if any(item.startswith("assert") for item in context):
                return True, "assertion-function"
            if tokens[max(start, use - 1):use + 2] == ["match", result_name, "with"]:
                branches = tokens[use + 2:window_end]
                has_ok = "Ok" in branches
                has_error = "Error" in branches
                has_failure = any(item.startswith("failwith") or item.startswith("assert") for item in branches)
                if has_ok and has_error and has_failure:
                    return True, "match-result"
            prefix = tokens[max(start, use - 4):use]
            suffix = tokens[use + 1:window_end]
            if "if" in prefix and "then" in suffix and any(item.startswith("failwith") for item in suffix):
                return True, "conditional-result-check"
        return False, ""

    # Also accept an inline result expression, such as assertEqual expected
    # (Subscription.renewMonthly ...), or a match directly over that call.
    prior_start = max(start, end - 64)
    prior = tokens[prior_start:end]
    last_let = max((index for index, item in enumerate(prior) if item == "let"), default=-1)
    if any(item.startswith("assert") for item in prior[last_let + 1:]):
        return True, "inline-assertion"
    if prior and prior[-1] == "match" and end < len(tokens) and tokens[end] == "with":
        branches = tokens[end + 1:min(len(tokens), end + 128)]
        if "Ok" in branches and "Error" in branches and any(
            item.startswith("failwith") or item.startswith("assert") for item in branches
        ):
            return True, "inline-match"
    return False, ""


def direct_test_result(tokens: list[str], calls: list[int], adapter_call: int, run_start: int) -> dict[str, str]:
    direct = [index for index in calls if index != adapter_call]
    if len(calls) != 2 or len(direct) != 1:
        raise AuditError("SelfTests.fs must contain one adapted callback call and one direct grouped renewal call")
    call = direct[0]
    # A result may be let-bound and checked later, or consumed inline by an
    # assertion/match. Local names and assertion idioms aren't task contracts.
    bind = None
    for opener in (call - 3, call - 4):
        if opener >= 0 and tokens[opener] == "let" and is_identifier(tokens[opener + 1]):
            if tokens[opener + 2:call] in (["="], ["=", "("]):
                bind = (tokens[opener + 1], opener)
                break
    actual_name = bind[0] if bind is not None else None
    cursor = call + 3
    for _ in range(3):
        _, cursor = parse_simple_path(tokens, cursor)
    record, timing_name, end = parse_timing_argument(tokens, cursor)
    if record is None:
        if timing_name is None or len(timing_name) != 1:
            raise AuditError("direct grouped renewal must pass a RenewalTiming record or local value")
        record = resolve_local_timing(tokens[run_start:call], timing_name[0], call - run_start,
                                      None, None, "direct grouped test")
    if "At" not in record or "ExpiresAt" not in record:
        raise AuditError("direct grouped regression must pass a RenewalTiming with At and ExpiresAt")
    call_end = end
    if call_end < len(tokens) and tokens[call_end] == ")" and bind is not None and bind[1] == call - 4:
        call_end += 1
    checked, style = _has_assertion_use(tokens, call_end, len(tokens), actual_name)
    if not checked and actual_name is None:
        checked, style = _has_assertion_use(tokens, run_start, call, None)
    if not checked:
        raise AuditError("direct grouped renewal result must be checked by an assertion or result match")
    return {
        "actualResult": actual_name or "inline API result",
        "resultCheck": style,
        "apiCallToken": str(call),
    }


def selftests_preserved(candidate: list[str], baseline: list[str]) -> dict[str, object]:
    c_monthly, c_annual = runner_anchors(candidate)
    b_monthly, b_annual = runner_anchors(baseline)
    c_run = main_run_anchor(candidate)
    # Treat the seed as an ordered token stream with local additions. A moved
    # new test can share common F# tokens (for example Subscription.renewMonthly)
    # with the monthly runner, which makes SequenceMatcher call an insertion a
    # replacement. Greedy ordered matching anchors on the old runner and keeps
    # every inherited token in order while permitting added code before it.
    candidate_cursor = 0
    matched_candidate: list[int] = []
    for seed_index, token in enumerate(baseline):
        while candidate_cursor < len(candidate) and candidate[candidate_cursor] != token:
            candidate_cursor += 1
        if candidate_cursor >= len(candidate):
            raise AuditError(
                f"an inherited SelfTests.fs token is missing or reordered near seed token {seed_index}"
            )
        matched_candidate.append(candidate_cursor)
        candidate_cursor += 1

    # Keep exact quoted assertion and error text, including spaces, and guard
    # against a repeated literal later in the file masking a removed case.
    seed_literals = Counter(token for token in baseline if token.startswith(('"', '@"')))
    candidate_literals = Counter(token for token in candidate if token.startswith(('"', '@"')))
    missing_literals = [literal for literal, count in seed_literals.items() if candidate_literals[literal] < count]
    if missing_literals:
        raise AuditError("an inherited SelfTests.fs quoted literal changed or was removed")

    # The only source additions permitted in SelfTests are the callback
    # adaptation and one grouped regression anywhere in run().
    # Validate the unmatched candidate spans so arbitrary code can't be added
    # elsewhere in the module.
    insertions: list[dict[str, int]] = []
    previous = -1
    for seed_index, candidate_index in enumerate(matched_candidate):
        if candidate_index > previous + 1:
            start, end = previous + 1, candidate_index
            if not (c_run <= start <= end <= len(candidate)):
                raise AuditError("SelfTests.fs additions must stay inside run()")
            insertions.append({"seedToken": seed_index, "candidateStart": start, "candidateEnd": end})
        previous = candidate_index
    if previous + 1 < len(candidate):
        start, end = previous + 1, len(candidate)
        if not (c_run <= start <= end <= len(candidate)):
            raise AuditError("SelfTests.fs additions must stay inside run()")
        insertions.append({"seedToken": len(baseline), "candidateStart": start, "candidateEnd": end})
    direct_end, callback_start = parse_scenario_adapter(candidate, c_monthly, baseline)
    if direct_end > c_annual:
        raise AuditError("monthly scenario adapter overlaps the annual scenario")
    # The callback API occurrence is the first RenewalTiming adaptation call.
    callback_call = find_one(candidate[c_monthly:direct_end], ["Subscription", ".", "renewMonthly"], "monthly callback call") + c_monthly
    direct = direct_test_result(candidate, find_api_calls(candidate), callback_call, c_run)
    return {
        "status": "pass",
        "seedTokenCount": len(baseline),
        "candidateTokenCount": len(candidate),
        "permittedInsertionCount": len(insertions),
        "insertions": insertions,
        "monthlyCallbackCallToken": callback_call,
        "directGroupedTest": direct,
        "quotedLiterals": "exact-token comparison",
        "formatting": "whitespace and comments outside quoted literals are ignored",
    }


def source_inventory(directory: Path) -> list[str]:
    extras: list[str] = []
    if not directory.is_dir():
        raise AuditError(f"source directory is missing: {directory}")
    for path in directory.rglob("*"):
        relative = path.relative_to(directory)
        if path.is_symlink():
            raise AuditError(f"refusing symlink in candidate source tree: {relative.as_posix()}")
        if not path.is_file() or any(part.casefold() in GENERATED_DIRS for part in relative.parts):
            continue
        if path.suffix.casefold() in SOURCE_SUFFIXES:
            if relative.as_posix() not in REQUIRED_FILES:
                extras.append(relative.as_posix())
        if path.name.casefold() in {"nuget.config", "directory.packages.props", "directory.build.props", "directory.build.targets"}:
            if relative.as_posix() not in REQUIRED_FILES:
                extras.append(relative.as_posix())
    return sorted(set(extras))


def audit(candidate: Path, baseline: Path = DEFAULT_BASELINE) -> dict[str, object]:
    candidate = candidate.expanduser().resolve()
    baseline = baseline.expanduser().resolve()
    failures: list[str] = []
    checks: dict[str, object] = {}
    try:
        for path in (candidate, baseline):
            if not path.is_dir():
                raise AuditError(f"source directory is missing: {path}")
        missing = [name for name in REQUIRED_FILES if not (candidate / name).is_file()]
        if missing:
            raise AuditError("candidate is missing seed files: " + ", ".join(missing))
        protected: dict[str, dict[str, object]] = {}
        for name in PROTECTED_FILES:
            seed = (baseline / name).read_bytes()
            current = (candidate / name).read_bytes()
            protected[name] = {
                "seedSha256": sha256(seed),
                "candidateSha256": sha256(current),
                "byteIdentical": seed == current,
            }
            if seed != current:
                failures.append(f"protected seed file changed: {name}")
        extras = source_inventory(candidate)
        if extras:
            failures.append("unallowed source/project files added: " + ", ".join(extras))
        checks["protectedFiles"] = protected
        checks["addedSourceFiles"] = extras
        operations_candidate = read_tokens(candidate / "Operations.fs")
        operations_baseline = read_tokens(baseline / "Operations.fs")
        try:
            operations_preserved(operations_candidate, operations_baseline)
            checks["operations"] = {"status": "pass", "allowedChanges": ["RenewalTiming", "renewMonthly", "renewBatchStep Monthly branch"]}
        except AuditError as error:
            failures.append(str(error))
            checks["operations"] = {"status": "failure", "detail": str(error)}
        selftests_candidate = read_tokens(candidate / "SelfTests.fs")
        selftests_baseline = read_tokens(baseline / "SelfTests.fs")
        try:
            checks["selfTests"] = selftests_preserved(selftests_candidate, selftests_baseline)
        except AuditError as error:
            failures.append(str(error))
            checks["selfTests"] = {"status": "failure", "detail": str(error)}
    except (OSError, AuditError) as error:
        failures.append(str(error))
    return {
        "status": "pass" if not failures else "failure",
        "candidate": str(candidate),
        "baseline": str(baseline),
        "sourceCodeExecuted": False,
        "failures": failures,
        "checks": checks,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, default=DEFAULT_CANDIDATE,
                        help="F# source directory to audit (default: prepared F# actor)")
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE,
                        help="trusted accepted 228 F# endpoint directory")
    parser.add_argument("--output", type=Path,
                        help="optional JSON receipt path; stdout always contains the report")
    args = parser.parse_args()
    result = audit(args.candidate, args.baseline)
    raw = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(raw, encoding="utf-8", newline="")
    print(raw, end="")
    return 0 if result["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
