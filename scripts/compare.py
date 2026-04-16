#!/usr/bin/env python3
"""
compare.py — Read connection-metrics JSON files and emit a markdown
comparison table for the Debian/Ubuntu base-image hypothesis test.

Usage:
    python scripts/compare.py results/          # Emit full markdown report to stdout
    python scripts/compare.py --single FILE     # Print a summary of one result file
"""

import json
import os
import sys
from pathlib import Path

# Ordered list of variants for column layout
VARIANT_ORDER = [
    "net9-debian", "net9-ubuntu", "net9-noble-chiseled",
    "net10-ubuntu", "net10-noble-chiseled", "net10-azurelinux",
]

VARIANT_DESCRIPTIONS = {
    "net9-debian":          ".NET 9 / Debian bookworm (aspnet:9.0)",
    "net9-ubuntu":          ".NET 9 / Ubuntu noble (aspnet:9.0-noble)",
    "net9-noble-chiseled":  ".NET 9 / Ubuntu noble chiseled (aspnet:9.0-noble-chiseled)",
    "net10-ubuntu":         ".NET 10 / Ubuntu noble (aspnet:10.0)",
    "net10-noble-chiseled": ".NET 10 / Ubuntu noble chiseled (aspnet:10.0-noble-chiseled)",
    "net10-azurelinux":     ".NET 10 / Azure Linux 3.0 (aspnet:10.0-azurelinux3.0)",
}


def load_results(results_dir):
    """Load all result JSON files from the directory."""
    results = {}
    for path in Path(results_dir).glob("*.json"):
        label = path.stem
        try:
            with open(path) as f:
                data = json.load(f)
            if data:
                results[label] = data
        except (json.JSONDecodeError, IOError):
            pass
    return results


def fmt(value):
    """Format a numeric value for the table."""
    if isinstance(value, (int,)):
        return str(value)
    if isinstance(value, float):
        return f"{value:.1f}"
    return str(value)


def get_metric(data, section, key):
    """Safely extract a metric value."""
    section_data = data.get(section, {})
    if isinstance(section_data, dict):
        return section_data.get(key, "—")
    return "—"


def compute_delta(baseline, current):
    """Compute delta string between baseline and current value."""
    if baseline == "—" or current == "—":
        return "—"
    try:
        b = float(baseline)
        c = float(current)
        diff = c - b
        if b == 0:
            return f"+{diff:.1f}" if diff > 0 else f"{diff:.1f}"
        pct = (diff / b) * 100
        sign = "+" if diff > 0 else ""
        if abs(pct) >= 20:
            return f"**{sign}{diff:.1f} ms ({sign}{pct:.0f}%)**"
        return f"{sign}{diff:.1f} ms ({sign}{pct:.0f}%)"
    except (ValueError, TypeError):
        return "—"


def build_section_table(results, section_key, section_title, variants):
    """Build a markdown table for one metric section."""
    rows = ["count", "p50", "p95", "p99", "max", "mean"]
    if section_key == "connectionSetup":
        rows.append("over500ms")

    header_labels = []
    for v in variants:
        desc = VARIANT_DESCRIPTIONS.get(v, v)
        header_labels.append(v)

    lines = []
    lines.append(f"### {section_title}\n")

    # Table header
    cols = ["Metric"] + header_labels
    lines.append("| " + " | ".join(cols) + " |")
    lines.append("|" + "|".join(["---"] * len(cols)) + "|")

    # Row labels for display
    display_names = {
        "count": "count",
        "p50": "p50 (ms)",
        "p95": "p95 (ms)",
        "p99": "p99 (ms)",
        "max": "max (ms)",
        "mean": "mean (ms)",
        "over500ms": "> 500 ms",
    }

    for row_key in rows:
        display = display_names.get(row_key, row_key)
        values = []
        for v in variants:
            data = results.get(v, {})
            val = get_metric(data, section_key, row_key)
            values.append(fmt(val))
        lines.append("| " + " | ".join([display] + values) + " |")

    lines.append("")
    return "\n".join(lines)


def generate_report(results):
    """Generate the full markdown report."""
    # Determine which variants we actually have data for
    available = [v for v in VARIANT_ORDER if v in results]
    if not available:
        return "No results found.\n"

    lines = []
    lines.append("# Base-Image Hypothesis Test: Connection-Establishment Metrics\n")
    lines.append("Testing whether the .NET 10 HTTPS connection-establishment latency regression")
    lines.append("([dotnet/runtime#124888](https://github.com/dotnet/runtime/issues/124888)) is caused")
    lines.append("by the Debian → Ubuntu switch in default .NET 10 Docker base images.\n")

    # Matrix description
    lines.append("## Test Matrix\n")
    lines.append("| Label | Runtime | Base Image | OS |")
    lines.append("|---|---|---|---|")
    image_info = {
        "net9-debian":          ("aspnet:9.0", "Debian 12 (bookworm)"),
        "net9-ubuntu":          ("aspnet:9.0-noble", "Ubuntu 24.04 (noble)"),
        "net9-noble-chiseled":  ("aspnet:9.0-noble-chiseled", "Ubuntu 24.04 (noble chiseled)"),
        "net10-ubuntu":         ("aspnet:10.0", "Ubuntu 24.04 (noble)"),
        "net10-noble-chiseled": ("aspnet:10.0-noble-chiseled", "Ubuntu 24.04 (noble chiseled)"),
        "net10-azurelinux":     ("aspnet:10.0-azurelinux3.0", "Azure Linux 3.0"),
    }
    for v in available:
        data = results[v]
        runtime = data.get("runtimeVersion", "?")
        img, os_name = image_info.get(v, ("?", "?"))
        lines.append(f"| {v} | {runtime} | `{img}` | {os_name} |")
    lines.append("")

    # Collection info
    lines.append("## Collection Parameters\n")
    lines.append("- **PooledConnectionLifetime**: 30 seconds")
    durations = [results[v].get("collectionDurationMinutes", "?") for v in available]
    lines.append(f"- **Collection duration**: {', '.join(str(d) for d in durations)} minutes (per variant)")
    lines.append("- **Cosmos emulator**: `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`")
    lines.append("- **Connection mode**: Gateway (required for Linux emulator)")
    lines.append("")

    # Metric tables
    lines.append("## Results\n")
    lines.append(build_section_table(
        results, "connectionSetup",
        "Connection Setup (`Experimental.System.Net.Http.Connections`)", available))
    lines.append(build_section_table(
        results, "dnsLookup",
        "DNS Lookup (`Experimental.System.Net.NameResolution`)", available))
    lines.append(build_section_table(
        results, "socketConnect",
        "Socket Connect (`Experimental.System.Net.Sockets`)", available))
    lines.append(build_section_table(
        results, "tlsHandshake",
        "TLS Handshake (`Experimental.System.Net.Security`)", available))

    # Delta analysis
    baseline_label = "net9-debian"
    if baseline_label in results:
        lines.append("### Deltas vs Baseline (net9-debian)\n")
        delta_sections = [
            ("connectionSetup", "Connection Setup"),
            ("dnsLookup", "DNS Lookup"),
        ]
        for section_key, section_title in delta_sections:
            delta_cols = ["Metric"] + [v for v in available if v != baseline_label]
            lines.append(f"**{section_title}**\n")
            lines.append("| " + " | ".join(delta_cols) + " |")
            lines.append("|" + "|".join(["---"] * len(delta_cols)) + "|")
            for row_key in ["p50", "p95", "p99", "mean"]:
                display = f"{row_key} (ms)"
                baseline_val = get_metric(results[baseline_label], section_key, row_key)
                vals = [display]
                for v in available:
                    if v == baseline_label:
                        continue
                    current_val = get_metric(results[v], section_key, row_key)
                    vals.append(compute_delta(baseline_val, current_val))
                lines.append("| " + " | ".join(vals) + " |")
            lines.append("")

    # Summary placeholder
    lines.append("## Analysis\n")
    lines.append("<!-- Replace this section with your analysis after reviewing the data -->\n")

    # Interpret results automatically if we have enough data
    if baseline_label in results and "net9-ubuntu" in results and "net10-ubuntu" in results:
        bl = results[baseline_label]
        n9u = results["net9-ubuntu"]
        n10u = results["net10-ubuntu"]

        bl_p95 = get_metric(bl, "connectionSetup", "p95")
        n9u_p95 = get_metric(n9u, "connectionSetup", "p95")
        n10u_p95 = get_metric(n10u, "connectionSetup", "p95")

        try:
            bl_p95 = float(bl_p95)
            n9u_p95 = float(n9u_p95)
            n10u_p95 = float(n10u_p95)

            n9_image_delta_pct = ((n9u_p95 - bl_p95) / bl_p95) * 100 if bl_p95 > 0 else 0
            runtime_delta_pct = ((n10u_p95 - n9u_p95) / n9u_p95) * 100 if n9u_p95 > 0 else 0

            if abs(n9_image_delta_pct) < 20 and runtime_delta_pct > 20:
                lines.append("The data suggests the regression is **runtime-internal** (.NET 10 vs .NET 9),")
                lines.append("not caused by the Debian → Ubuntu base-image switch. Switching .NET 9 from Debian")
                lines.append(f"to Ubuntu shows a ~{n9_image_delta_pct:+.0f}% change in connection setup p95,")
                lines.append(f"while the .NET 9 → .NET 10 jump on Ubuntu shows ~{runtime_delta_pct:+.0f}%.\n")
            elif abs(n9_image_delta_pct) > 20:
                lines.append("The data suggests the base image **does contribute** to connection-establishment")
                lines.append(f"differences. Switching .NET 9 from Debian to Ubuntu alone shows ~{n9_image_delta_pct:+.0f}%")
                lines.append("change in connection setup p95, supporting the Debian → Ubuntu hypothesis.\n")
            else:
                lines.append("The results are inconclusive — deltas between variants are within noise range.")
                lines.append("Consider running with a longer collection window or more iterations.\n")
        except (ValueError, TypeError):
            lines.append("_Unable to auto-analyze — check the raw numbers above._\n")

    # Reproduction commands
    lines.append("## Reproduction\n")
    lines.append("```bash")
    lines.append("# Run the full 4-variant matrix (~60 min with 15-min collection per variant)")
    lines.append("./scripts/run-matrix.sh")
    lines.append("")
    lines.append("# Or with a shorter collection window for a quick sanity check")
    lines.append("./scripts/run-matrix.sh 5")
    lines.append("")
    lines.append("# Generate comparison table from existing results")
    lines.append("python scripts/compare.py results/ > RESULTS.md")
    lines.append("```\n")

    return "\n".join(lines)


def print_single(filepath):
    """Print a summary of a single result file."""
    with open(filepath) as f:
        data = json.load(f)

    label = Path(filepath).stem
    print(f"\n── {label} ──")
    print(f"  Runtime:    {data.get('runtimeVersion', '?')}")
    print(f"  Duration:   {data.get('collectionDurationMinutes', '?')} min")

    for section in ["connectionSetup", "dnsLookup", "socketConnect", "tlsHandshake"]:
        s = data.get(section, {})
        if not s or s.get("count", 0) == 0:
            continue
        extra = f"  >500ms: {s.get('over500ms', 0)}" if section == "connectionSetup" else ""
        print(f"  {section}: count={s.get('count',0)}  "
              f"p50={fmt(s.get('p50',0))}  p95={fmt(s.get('p95',0))}  "
              f"p99={fmt(s.get('p99',0))}  mean={fmt(s.get('mean',0))}{extra}")


if __name__ == "__main__":
    # Ensure stdout handles Unicode (arrows, em-dashes, etc.)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} RESULTS_DIR", file=sys.stderr)
        print(f"       {sys.argv[0]} --single FILE", file=sys.stderr)
        sys.exit(1)

    if sys.argv[1] == "--single":
        if len(sys.argv) < 3:
            print("ERROR: --single requires a file path", file=sys.stderr)
            sys.exit(1)
        print_single(sys.argv[2])
    else:
        results_dir = sys.argv[1]
        results = load_results(results_dir)
        if not results:
            print(f"No result files found in {results_dir}", file=sys.stderr)
            sys.exit(1)
        print(generate_report(results))
