"""Normalized structural comparison of two Campfire room pages.

Parses both pages into element trees (stdlib html.parser), masks per-request
values (CSRF/authenticity tokens, CSP nonces), collapses whitespace in text, and
compares element structure, attributes and text. Prints a summary and the
first differences; exits 0 only when the pages are structurally identical.

Usage: python scripts/compare_html_structure.py REFERENCE.html CANDIDATE.html
           [--max N] [--json OUT] [--mask-deployment]

--mask-deployment also masks values that legitimately differ between two
deployments of the same app: fingerprinted asset digests (name-<hex>.ext),
the scheme and host of absolute URLs, and the importmap script text.

Reference pages captured from the Rails app hold fixture data and stay
private (never committed); only this tool and its summaries are shared.
"""
from __future__ import annotations

import difflib
import html.parser
import json
import re
import sys

VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"}
MASKED_ATTRIBUTES = {"nonce"}
MASKED_INPUT_NAMES = {"authenticity_token"}
MASKED_META_NAMES = {"csrf-token", "csp-nonce"}


MASK_DEPLOYMENT = False
DIGEST = re.compile(r"-[0-9a-f]{8,64}(?=\.(?:js|css|png|svg|ico|woff2?|webp|jpg|gif|mp3)\b)")
ORIGIN = re.compile(r"https?://[^/\s\"']+")


def mask_deployment(value):
    return ORIGIN.sub("<origin>", DIGEST.sub("-<digest>", value))


class Node:
    __slots__ = ("tag", "attrs", "children", "text")

    def __init__(self, tag, attrs=None):
        self.tag = tag
        self.attrs = attrs or {}
        self.children = []
        self.text = None


class TreeBuilder(html.parser.HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = Node("#document")
        self.stack = [self.root]
        self.in_importmap = False

    def handle_starttag(self, tag, attrs):
        node = Node(tag, normalize_attrs(tag, attrs))
        self.stack[-1].children.append(node)
        self.in_importmap = tag == "script" and node.attrs.get("type") == "importmap"
        if tag not in VOID:
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        self.stack[-1].children.append(Node(tag, normalize_attrs(tag, attrs)))

    def handle_endtag(self, tag):
        for index in range(len(self.stack) - 1, 0, -1):
            if self.stack[index].tag == tag:
                del self.stack[index:]
                return

    def handle_data(self, data):
        text = re.sub(r"\s+", " ", data).strip()
        if text and MASK_DEPLOYMENT:
            text = "<importmap>" if self.in_importmap else mask_deployment(text)
        if text:
            node = Node("#text")
            node.text = text
            self.stack[-1].children.append(node)


def normalize_attrs(tag, attrs):
    result = {}
    values = dict(attrs)
    for name, value in attrs:
        value = "" if value is None else value
        if name in MASKED_ATTRIBUTES:
            value = "<masked>"
        if tag == "input" and name == "value" and values.get("name") in MASKED_INPUT_NAMES:
            value = "<masked>"
        if tag == "meta" and name == "content" and values.get("name") in MASKED_META_NAMES:
            value = "<masked>"
        if MASK_DEPLOYMENT:
            value = mask_deployment(value)
        result[name] = value
    return result


def parse(path):
    builder = TreeBuilder()
    with open(path, encoding="utf-8") as handle:
        builder.feed(handle.read())
    return builder.root


def label(node):
    if node.tag == "#text":
        return "#text"
    ident = node.attrs.get("id")
    classes = node.attrs.get("class", "").split()
    suffix = f"#{ident}" if ident else (f".{classes[0]}" if classes else "")
    return node.tag + suffix


def signature(node):
    if node.tag == "#text":
        return "#text"
    keys = ("id", "class", "name", "rel", "href", "src", "type")
    return node.tag + "|" + "|".join(node.attrs.get(key, "") for key in keys)


TOTALS = {"missingNodes": 0, "extraNodes": 0}


def compare(reference, candidate, path, out, limit):
    if len(out) >= limit:
        return
    here = path + "/" + label(reference)
    if reference.tag != candidate.tag:
        out.append({"path": here, "kind": "tag", "reference": reference.tag, "candidate": candidate.tag})
        return
    if reference.tag == "#text":
        if reference.text != candidate.text:
            out.append({"path": here, "kind": "text", "reference": reference.text[:160], "candidate": candidate.text[:160]})
        return
    if reference.attrs != candidate.attrs:
        missing = sorted(set(reference.attrs) - set(candidate.attrs))
        extra = sorted(set(candidate.attrs) - set(reference.attrs))
        changed = sorted(name for name in set(reference.attrs) & set(candidate.attrs) if reference.attrs[name] != candidate.attrs[name])
        out.append({"path": here, "kind": "attrs", "missing": missing, "extra": extra,
                    "changed": {name: [reference.attrs[name][:120], candidate.attrs[name][:120]] for name in changed}})
    ref_children, cand_children = reference.children, candidate.children
    # Align siblings so one inserted or missing element is reported once.
    matcher = difflib.SequenceMatcher(None, [signature(c) for c in ref_children], [signature(c) for c in cand_children], autojunk=False)
    for op, i1, i2, j1, j2 in matcher.get_opcodes():
        if len(out) >= limit:
            return
        if op == "equal":
            for left, right in zip(ref_children[i1:i2], cand_children[j1:j2]):
                compare(left, right, here, out, limit)
        elif op == "replace" and (i2 - i1) == (j2 - j1):
            for left, right in zip(ref_children[i1:i2], cand_children[j1:j2]):
                compare(left, right, here, out, limit)
        else:
            if i2 > i1:
                nodes = sum(count(c) for c in ref_children[i1:i2])
                TOTALS["missingNodes"] += nodes
                out.append({"path": here, "kind": "missing", "elements": [label(c) for c in ref_children[i1:i2]][:12], "count": i2 - i1, "nodes": nodes})
            if j2 > j1:
                nodes = sum(count(c) for c in cand_children[j1:j2])
                TOTALS["extraNodes"] += nodes
                out.append({"path": here, "kind": "extra", "elements": [label(c) for c in cand_children[j1:j2]][:12], "count": j2 - j1, "nodes": nodes})


def count(node):
    return 1 + sum(count(child) for child in node.children)


def main():
    global MASK_DEPLOYMENT
    args = sys.argv[1:]
    MASK_DEPLOYMENT = "--mask-deployment" in args
    args = [a for a in args if a != "--mask-deployment"]
    limit = 40
    json_out = None
    if "--max" in args:
        limit = int(args[args.index("--max") + 1])
    if "--json" in args:
        json_out = args[args.index("--json") + 1]
    reference_path, candidate_path = args[0], args[1]
    reference, candidate = parse(reference_path), parse(candidate_path)
    differences = []
    compare(reference, candidate, "", differences, 1_000_000)
    summary = {"referenceNodes": count(reference), "candidateNodes": count(candidate), "differences": len(differences),
               "missingReferenceNodes": TOTALS["missingNodes"], "extraCandidateNodes": TOTALS["extraNodes"],
               "printedDifferences": min(limit, len(differences))}
    if json_out:
        with open(json_out, "w", encoding="utf-8") as handle:
            json.dump({"summary": summary, "differences": differences}, handle, ensure_ascii=False, indent=1)
    try:
        print(json.dumps(summary))
        for item in differences[:limit]:
            print(json.dumps(item))
    except (BrokenPipeError, OSError):
        pass  # the reader closed the pipe (e.g. `| head`); the exit code still reports the result
    return 0 if not differences else 1


if __name__ == "__main__":
    sys.exit(main())
