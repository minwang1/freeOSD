"""Pure analysis helpers for monitor OSD/VCP identification reports."""


def compare_snapshots(before, after):
    """Compare two VCP snapshots while retaining unreadable and unchanged codes."""
    changed = {}
    unchanged = []
    unreadable = []
    for code in sorted(set(before) | set(after)):
        left = before.get(code)
        right = after.get(code)
        left_read = left.get("read") if left else None
        right_read = right.get("read") if right else None
        if left_read is None or right_read is None:
            unreadable.append({
                "code": code,
                "before_error": left.get("read_error") if left else "not captured",
                "after_error": right.get("read_error") if right else "not captured",
            })
            continue
        changed_fields = [
            field for field in ("current", "maximum", "type")
            if left_read.get(field) != right_read.get(field)
        ]
        if changed_fields:
            entry = {
                "name": (right or left).get("name"),
                "changed_fields": changed_fields,
                "before": left_read,
                "after": right_read,
            }
            if "current" in changed_fields:
                entry["current_difference"] = right_read["current"] - left_read["current"]
            changed[code] = entry
        else:
            unchanged.append(code)
    return {
        "changed_vcp": changed,
        "unchanged_readable_codes": unchanged,
        "unreadable_codes": unreadable,
    }


def analyze_osd_controls(osd_controls):
    """Build state-to-VCP and VCP-to-state summaries from labeled observations."""
    analyzed_controls = []
    for control in osd_controls:
        observations = control.get("observations", [])
        comparisons = []
        code_values = {}
        state_values = {}

        for observation in observations:
            state = str(observation.get("state", ""))
            snapshot = observation.get("snapshot", {})
            readable = {}
            for code, item in snapshot.items():
                if not isinstance(item, dict):
                    continue
                value = item.get("read", {}).get("current") if isinstance(item.get("read"), dict) else None
                if value is None:
                    continue
                readable[code] = value
                code_values.setdefault(code, []).append({
                    "state": state,
                    "current": value,
                    "probe_only": bool(item.get("probe_only", False)),
                })
            state_values.setdefault(state, {}).setdefault("observations", 0)
            state_values[state]["observations"] += 1
            state_values[state].setdefault("vcp_values", {})
            for code, value in readable.items():
                state_values[state]["vcp_values"].setdefault(code, set()).add(value)

        for previous, current in zip(observations, observations[1:]):
            delta = compare_snapshots(
                previous.get("snapshot", {}), current.get("snapshot", {})
            )
            comparisons.append({
                "from_state": previous.get("state", ""),
                "to_state": current.get("state", ""),
                "from_captured_at": previous.get("captured_at"),
                "to_captured_at": current.get("captured_at"),
                **delta,
            })

        correlations = []
        for code, samples in sorted(code_values.items()):
            by_value = {}
            for sample in samples:
                by_value.setdefault(sample["current"], {"osd_states": [], "observations": 0})
                value_group = by_value[sample["current"]]
                if sample["state"] not in value_group["osd_states"]:
                    value_group["osd_states"].append(sample["state"])
                value_group["observations"] += 1
            if len(by_value) < 2:
                continue
            probe_only = all(sample["probe_only"] for sample in samples)
            probe_only_in_some_captures = any(sample["probe_only"] for sample in samples)
            advertised = any(not sample["probe_only"] for sample in samples)
            changed_transitions = sum(
                1 for comparison in comparisons
                if code in comparison["changed_vcp"]
                and "current" in comparison["changed_vcp"][code]["changed_fields"]
            )
            correlations.append({
                "code": code,
                "name": next((
                    observation.get("snapshot", {}).get(code, {}).get("name")
                    for observation in observations
                    if observation.get("snapshot", {}).get(code, {}).get("name")
                ), "Unmapped / monitor-specific feature"),
                "advertised": advertised,
                "probe_only": probe_only,
                "probe_only_in_some_captures": probe_only_in_some_captures,
                "changed_transitions": changed_transitions,
                "value_to_osd_states": [
                    {
                        "current": value,
                        "osd_states": details["osd_states"],
                        "observations": details["observations"],
                    }
                    for value, details in sorted(by_value.items())
                ],
            })

        analyzed_controls.append({
            "osd_option": control.get("osd_option", ""),
            "state_to_vcp_values": [
                {
                    "state": state,
                    "observations": details["observations"],
                    "vcp_values": {
                        code: sorted(values)
                        for code, values in sorted(details["vcp_values"].items())
                    },
                }
                for state, details in state_values.items()
            ],
            "vcp_correlations": correlations,
            "comparisons": comparisons,
        })
    return analyzed_controls


def format_analysis_summary(report, analyzed_controls):
    """Format a concise, human-readable summary for the import-analysis window."""
    monitor = report.get("monitor", {})
    lines = [
        f"Monitor: {monitor.get('description', 'Unknown')} ({monitor.get('display', 'unknown display')})",
        f"Capture mode: {report.get('capture_mode', 'unknown')}",
        f"OSD options analyzed: {len(analyzed_controls)}",
        "",
    ]
    for control in analyzed_controls:
        lines.append(f"OSD option: {control['osd_option']}")
        state_rows = control["state_to_vcp_values"]
        lines.append("  Captured states: " + (" → ".join(row["state"] for row in state_rows) or "none"))
        if control["vcp_correlations"]:
            lines.append("  VCP value associations:")
            for correlation in control["vcp_correlations"]:
                source = "advertised" if correlation["advertised"] else "probe only"
                if correlation["probe_only_in_some_captures"] and correlation["advertised"]:
                    source = "advertised in some captures; probe only in others"
                values = "; ".join(
                    f"{item['current']} → {', '.join(item['osd_states'])} "
                    f"({item['observations']} capture(s))"
                    for item in correlation["value_to_osd_states"]
                )
                lines.append(
                    f"    {correlation['code']} {correlation['name']} [{source}]: {values}; "
                    f"changed transitions={correlation['changed_transitions']}"
                )
        else:
            lines.append("  VCP value associations: no readable current value changed across these states")

        if control["comparisons"]:
            lines.append("  Consecutive-state changes:")
            for comparison in control["comparisons"]:
                changes = []
                for code, change in comparison["changed_vcp"].items():
                    before = change.get("before", {}).get("current")
                    after = change.get("after", {}).get("current")
                    changes.append(f"{code} {before} → {after}" if before is not None and after is not None else code)
                unreadable_count = len(comparison["unreadable_codes"])
                lines.append(
                    f"    {comparison['from_state']} → {comparison['to_state']}: "
                    + (", ".join(changes) if changes else "no readable VCP change")
                    + f"; unreadable codes={unreadable_count}"
                )
        lines.append("")
    return "\n".join(lines).rstrip()
