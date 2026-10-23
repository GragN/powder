"""Compare actual window throughput using the same finite frame count.

Requires Python 3, a graphical desktop and two already-built Powder DLLs.
The measured interval includes startup and a final screenshot; it is not a GPU benchmark.
"""

import argparse
import json
import os
import platform
import subprocess
import struct
import tempfile
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", required=True, type=Path)
    parser.add_argument("--after", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--scene", choices=["boiling", "air", "empty"], default="boiling")
    parser.add_argument("--frames", type=int, default=240)
    parser.add_argument("--output", type=Path, default=Path("artifacts/window-benchmark.json"))
    args = parser.parse_args()
    if args.frames <= 0:
        parser.error("--frames must be positive")
    for dll in [args.before, args.after]:
        if not dll.is_file():
            parser.error(f"DLL not found: {dll}")
    results = []
    environment = dict(os.environ, DOTNET_TieredCompilation="0")
    for version, dll in [("before", args.before.resolve()), ("after", args.after.resolve())]:
        command = [args.dotnet, str(dll), "--frames", str(args.frames)]
        if args.scene != "empty":
            command += ["--boiling-demo" if args.scene == "boiling" else "--air-demo"]
        print(f"Measuring {version}: {args.scene}, {args.frames} frames", flush=True)
        with tempfile.TemporaryDirectory(prefix="powder-window-") as directory:
            marker = Path(directory) / "completed.png"
            command += ["--screenshot", str(marker)]
            started = time.perf_counter()
            process = subprocess.run(command, cwd=dll.parent, env=environment, text=True,
                                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
            elapsed = time.perf_counter() - started
            if process.returncode or not marker.is_file():
                raise RuntimeError(f"{version} did not complete the frame limit ({process.returncode}):\n{process.stdout}")
            header = marker.read_bytes()[:24]
            if header[:8] != b"\x89PNG\r\n\x1a\n":
                raise RuntimeError(f"{version} did not produce a PNG screenshot")
            width, height = struct.unpack(">II", header[16:24])
        result = dict(Version=version, ElapsedSeconds=elapsed, FramesPerSecond=args.frames / elapsed,
                      WindowWidth=width, WindowHeight=height)
        results.append(result)
        print(f"{version}: {result['FramesPerSecond']:.2f} frames/s including startup", flush=True)
    report = dict(FormatVersion=2, Platform=platform.platform(), Scene=args.scene,
                  Frames=args.frames, IncludesStartup=True, IncludesFinalScreenshot=True, TieredCompilation=False,
                  Display=environment.get("DISPLAY"), SoftwareOpenGL=environment.get("LIBGL_ALWAYS_SOFTWARE"),
                  Measurements=results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Window report: {args.output}")


if __name__ == "__main__":
    main()
