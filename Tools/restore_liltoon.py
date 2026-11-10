"""Prepare an isolated Unity project for building an external lilToon pack."""
import hashlib
import io
import json
from pathlib import Path
from shutil import copyfile
from urllib.request import urlopen
from zipfile import ZipFile

REPOSITORY = "https://lilxyzw.github.io/vpm-repos/vpm.json"
PACKAGE = "jp.lilxyzw.liltoon"
VERSION = "2.4.1"
SHA256 = "90f8bed7cdd7435f4904bca3d885a81a1811bd25036d712f21942c531d940247"
REPO = Path(__file__).resolve().parents[1]
PROJECT = REPO / "Builds" / "LilToonPackProject"
ROOT = PROJECT / "Assets" / "lilToon"


def fetch(url, limit):
    with urlopen(url, timeout=60) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ValueError("VPM download exceeds size limit")
    return data


def restore():
    index = json.loads(fetch(REPOSITORY, 4 << 20).decode("utf-8-sig"))
    entry = index["packages"][PACKAGE]["versions"][VERSION]
    if (entry["name"], entry["version"], entry["zipSHA256"]) != (PACKAGE, VERSION, SHA256):
        raise ValueError("VPM release identity or checksum differs from the pin")
    data = fetch(entry["url"], 16 << 20)
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise ValueError("VPM ZIP checksum mismatch")
    with ZipFile(io.BytesIO(data)) as package:
        manifest = json.loads(package.read("package.json").decode("utf-8-sig"))
        if (manifest["name"], manifest["version"], manifest["repo"]) != (PACKAGE, VERSION, REPOSITORY):
            raise ValueError("Unexpected package.json in VPM release")
        for item in package.infolist():
            if item.is_dir():
                continue
            path = (ROOT / item.filename).resolve()
            if not path.is_relative_to(ROOT.resolve()):
                raise ValueError("Invalid package path")
            content = package.read(item)
            if path.is_file():
                previous = path.read_bytes()
                if previous == content or (b"\0" not in content and previous.replace(b"\r\n", b"\n") == content.replace(b"\r\n", b"\n")):
                    continue
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
    editor = PROJECT / "Assets" / "AvaAvaDance" / "Editor"
    editor.mkdir(parents=True, exist_ok=True)
    for source in (
        REPO / "Tools/LilToonPackBuilder.cs",
        REPO / "Assets/AvaAvaDance/Editor/ShaderPackBuilder.cs",
        REPO / "Assets/AvaAvaDance/Runtime/Import/ShaderLibrary.cs",
        REPO / "Assets/AvaAvaDance/Runtime/Import/VpmInstaller.cs",
    ):
        copyfile(source, editor / source.name)
    assembly = {"name": "AvaAvaDance.PackEditor", "references": ["lilToon.Editor"], "includePlatforms": ["Editor"]}
    (editor / "AvaAvaDance.PackEditor.asmdef").write_text(json.dumps(assembly, indent=2), encoding="utf-8")
    (PROJECT / "Assets/csc.rsp").write_text("-define:LILTOON_DISABLE_OPTIMIZATION\n", encoding="utf-8")
    settings = PROJECT / "ProjectSettings"
    settings.mkdir(exist_ok=True)
    copyfile(REPO / "ProjectSettings/ProjectVersion.txt", settings / "ProjectVersion.txt")
    packages = PROJECT / "Packages"
    packages.mkdir(exist_ok=True)
    locked = json.loads((REPO / "Packages/packages-lock.json").read_text(encoding="utf-8"))["dependencies"]
    modules = {name: entry["version"] for name, entry in locked.items() if entry["source"] == "builtin"}
    (packages / "manifest.json").write_text(json.dumps({"dependencies": modules}, indent=2), encoding="utf-8")
    print(f"Prepared {PROJECT} with {PACKAGE}@{VERSION}. SHA-256 {SHA256}")


if __name__ == "__main__":
    restore()
