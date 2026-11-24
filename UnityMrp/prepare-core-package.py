import gzip
import io
import json
import os
import subprocess
import tarfile
from argparse import ArgumentParser
from pathlib import Path
from tempfile import TemporaryDirectory


UNITY = Path(__file__).resolve().parent
CORE = UNITY.parent / "Core"
METADATA = UNITY / "CorePackageMetadata"
RELEASES = UNITY / "Packages" / "com.mrp.csharp.unity" / "CoreReleases~"

ASSEMBLIES = ("CSharpMrp.Core.dll", "CSharpSynth.dll", "NLayer.dll")
ASSETS = (
    "Runtime/Plugins/NLayer-LICENSE.txt",
    "Runtime/Resources/CSharpMrpGMBank.bytes",
    "Runtime/ThirdParty/CSharpSynth/ReadMe.txt",
)
META = (
    "package.json.meta",
    "Runtime.meta",
    "Runtime/Plugins.meta",
    "Runtime/Resources.meta",
    "Runtime/ThirdParty.meta",
    "Runtime/ThirdParty/CSharpSynth.meta",
)


def package_bytes(build):
    files = {"package.json": (METADATA / "package.json").read_bytes()}
    files.update({path: (METADATA / path).read_bytes() for path in META})
    for path in ASSETS:
        files[path] = (CORE / path).read_bytes()
        files[path + ".meta"] = (METADATA / (path + ".meta")).read_bytes()
    for assembly in ASSEMBLIES:
        path = f"Runtime/Plugins/{assembly}"
        files[path] = (build / assembly).read_bytes()
        files[path + ".meta"] = (METADATA / (path + ".meta")).read_bytes()

    result = io.BytesIO()
    with gzip.GzipFile(fileobj=result, mode="wb", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w") as archive:
            for path, data in sorted(files.items()):
                info = tarfile.TarInfo(f"package/{path}")
                info.size = len(data)
                info.mode = 0o644
                archive.addfile(info, io.BytesIO(data))
    return result.getvalue()


def main():
    parser = ArgumentParser(description="Build and pack a versioned CSharpMrp Unity release.")
    parser.add_argument("--check", action="store_true", help="Verify the archived release matches this build.")
    args = parser.parse_args()
    package = json.loads((METADATA / "package.json").read_text(encoding="utf-8"))
    filename = f"{package['name']}-{package['version']}.tgz"
    if Path(filename).name != filename:
        raise SystemExit("Invalid package name or version.")
    output = RELEASES / filename
    with TemporaryDirectory(prefix="mrp-core-upm-") as temp:
        build = Path(temp)
        subprocess.run([
            "dotnet", "build", str(CORE / "CSharpMrp.Core.csproj"),
            "--configuration", "Release", "--artifacts-path", str(build / "artifacts"),
            "--output", str(build / "bin"), "--nologo",
            "-p:EmbedGmBank=false", "-p:DebugType=None", "-p:DebugSymbols=false",
        ], check=True)
        data = package_bytes(build / "bin")
    if output.exists():
        if output.read_bytes() != data:
            raise SystemExit(f"Version {package['version']} already exists with different bytes. Bump the package version.")
        print(f"Verified Core package: {output}")
    elif args.check:
        raise SystemExit(f"Missing Core release: {output}")
    else:
        RELEASES.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(prefix="release-", dir=RELEASES) as staging:
            temp = Path(staging) / filename
            temp.write_bytes(data)
            # Publishing with a hard link fails if another writer published this version.
            try:
                os.link(temp, output)
            except FileExistsError:
                if output.read_bytes() != data:
                    raise
        print(f"Built Core package: {output}")


if __name__ == "__main__":
    main()
