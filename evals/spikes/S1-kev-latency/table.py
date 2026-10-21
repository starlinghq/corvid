"""Markdown tables from the spike's JSONL results: python3 -I table.py <file.jsonl>..."""

import json
import sys


def main(paths):
    print("| Model | Device | Cell | State (Corvid / Kev tokens) | Rows (Kev tokens) | n | p50 | p90 | p99 | mean | min | Kev only p50 | Load before → after |")
    print("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
    for path in paths:
        with open(path) as handle:
            for line in handle:
                r = json.loads(line)
                if r["kind"] != "cell":
                    continue
                f = r["fabric_ms"]
                rows = r["row_tokens_kev"]
                state = f"{min(r['state_tokens_corvid'])} / {r['state_ids_kev'][1]}"
                print(
                    f"| {r['model']} | {r['device']} | {r['cell']} | {state} | {rows[0]}–{rows[1]} | {r['calls']} "
                    f"| {f['p50']:.0f} | {f['p90']:.0f} | {f['p99']:.0f} | {f['mean']:.0f} | {f['min']:.0f} "
                    f"| {r['kev_decider_ms']['p50']:.0f} | {r['loadavg_before']} → {r['loadavg_after']} |"
                )


if __name__ == "__main__":
    main(sys.argv[1:])
