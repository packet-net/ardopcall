#!/usr/bin/env python3
"""Fails unless every binary bundles the latest .NET 10 runtime patch.

The binaries are self-contained single files, so each carries the runtime of whichever SDK built
it, and an operating system update never patches it. A runner with a stale SDK would ship months
of missed security fixes and nothing would notice. This reads the runtime version out of each
binary and compares it with the latest 10.0 runtime Microsoft lists.

Usage: scripts/check-bundled-runtime.py <binary>...
"""
import json
import re
import sys
import urllib.request
from pathlib import Path

INDEX = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
CHANNEL = "10.0"
RUNTIME = re.compile(rb"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.[a-z0-9-]+/(\d+\.\d+\.\d+)")


def latest_runtime():
    with urllib.request.urlopen(INDEX, timeout=30) as response:
        index = json.load(response)
    for channel in index["releases-index"]:
        if channel["channel-version"] == CHANNEL:
            return channel["latest-runtime"]
    sys.exit(f"no .NET {CHANNEL} channel in {INDEX}")


def main(binaries):
    if not binaries:
        sys.exit(__doc__)
    want = latest_runtime()
    print(f"latest .NET {CHANNEL} runtime: {want}")
    bad = False
    for binary in map(Path, binaries):
        found = sorted({m.decode() for m in RUNTIME.findall(binary.read_bytes())})
        print(f"{binary.name} bundles {', '.join(found) or 'nothing recognisable'}")
        if found != [want]:
            print(f"::error::{binary.name} bundles .NET runtime {', '.join(found) or 'unknown'}, "
                  f"not {want}. Update the .NET 10 SDK on the runner that built it.")
            bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
