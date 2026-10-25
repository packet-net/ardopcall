#!/usr/bin/env python3
"""Fails if any binary bundles a .NET 10 runtime older than the latest patch.

The binaries are self-contained single files, so each carries the runtime of whichever SDK built
it, and an operating system update never patches it. A runner with a stale SDK would ship months
of missed security fixes and nothing would notice. This reads the runtime version out of each
binary and compares it with the latest 10.0 runtime Microsoft lists.
A newer one passes: an SDK or workload can reach nuget.org a few days before the release index lists that patch
(the WebAssembly workload did, 10.0.14 on 2026-10-25), and the point is to catch a stale runner, not one ahead of the index.

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


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def main(binaries):
    if not binaries:
        sys.exit(__doc__)
    want = latest_runtime()
    print(f"latest .NET {CHANNEL} runtime: {want}")
    bad = False
    for binary in map(Path, binaries):
        found = sorted({m.decode() for m in RUNTIME.findall(binary.read_bytes())})
        print(f"{binary.name} bundles {', '.join(found) or 'nothing recognisable'}")
        if len(found) != 1:
            print(f"::error::{binary.name} bundles .NET runtime {', '.join(found) or 'unknown'}, "
                  f"not one recognisable version. Check how it was built.")
            bad = True
        elif version_key(found[0]) < version_key(want):
            print(f"::error::{binary.name} bundles .NET runtime {found[0]}, older than {want}. "
                  "Update the .NET 10 SDK on the runner that built it.")
            bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
