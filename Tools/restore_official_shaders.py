"""Prepare pinned official shader inputs; compile with Unity before packaging the app."""
import hashlib
import io
import json
import tarfile
from pathlib import Path
from shutil import copyfile
from urllib.request import Request, urlopen

import restore_liltoon

REPO = Path(__file__).resolve().parents[1]
VRM_VERSION = "0.131.4"
VRM_FILES = {
    "UniVRM-0.131.4_efd2.unitypackage": "3f0e7349f22914232c0621ce1a0f48b549c2553bc5d3308896cd4612730c445a",
    "VRM-0.131.4_efd2.unitypackage": "8f91761bfe9cb498f29f7d66b5750e64909078569ffbb397ecbcfa1edd881f27",
}
EXTENSIONS = {".shader", ".cginc", ".hlsl", ".hlslinc", ".glslinc", ".png", ".jpg", ".tga", ".exr", ".txt", ".md"}


def download(url, path, checksum):
    if path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest() == checksum:
        return path.read_bytes()
    with urlopen(Request(url, headers={"User-Agent": "AvaAvaDance"}), timeout=60) as response:
        data = response.read(32 * 1024 * 1024 + 1)
    if len(data) > 32 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != checksum:
        raise ValueError("Official shader source checksum mismatch: " + url)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return data


def restore():
    restore_liltoon.restore()
    project = REPO / "Builds/VrmShaderPackProject"
    for name, checksum in VRM_FILES.items():
        url = f"https://github.com/vrm-c/UniVRM/releases/download/v{VRM_VERSION}/{name}"
        data = download(url, REPO / "Builds/OfficialShaderSources" / name, checksum)
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as package:
            members = {item.name: item for item in package.getmembers() if item.isfile()}
            for key in members:
                if not key.endswith("/pathname"):
                    continue
                original = package.extractfile(members[key]).read().decode("utf-8-sig").strip()
                if not original.startswith(("Assets/", "Packages/")) or ".." in original.split("/"):
                    raise ValueError("Invalid UnityPackage path: " + original)
                if Path(original).suffix.lower() not in EXTENSIONS:
                    continue
                target = project / ("Assets/" + original.removeprefix("Packages/") if original.startswith("Packages/") else original)
                target.parent.mkdir(parents=True, exist_ok=True)
                prefix = key.removesuffix("pathname")
                for suffix, destination in (("asset", target), ("asset.meta", Path(str(target) + ".meta"))):
                    if prefix + suffix in members:
                        content = package.extractfile(members[prefix + suffix]).read()
                        if not destination.is_file() or destination.read_bytes() != content:
                            destination.write_bytes(content)
    editor = project / "Assets/AvaPack/Editor"
    editor.mkdir(parents=True, exist_ok=True)
    copyfile(REPO / "Assets/AvaAvaDance/Resources/PackageShaderBuilder.txt", editor / "PackageShaderBuilder.cs")
    settings = project / "ProjectSettings"
    settings.mkdir(exist_ok=True)
    copyfile(REPO / "ProjectSettings/ProjectVersion.txt", settings / "ProjectVersion.txt")
    packages = project / "Packages"
    packages.mkdir(exist_ok=True)
    (packages / "manifest.json").write_text('{"dependencies":{"com.unity.modules.assetbundle":"1.0.0","com.unity.modules.imageconversion":"1.0.0"}}', encoding="utf-8")
    licenses = REPO / "Assets/AvaAvaDance/Resources/ShaderLicenses"
    licenses.mkdir(parents=True, exist_ok=True)
    lil = REPO / "Builds/LilToonPackProject/Assets/lilToon"
    notices = [lil / "LICENSE", lil / "Third Party Notices.md", lil / "Shader/Includes/VRC Light Volumes/license.txt"]
    (licenses / "liltoon.txt").write_text("\n\n".join(p.read_text(encoding="utf-8-sig") for p in notices), encoding="utf-8")
    vrm_licenses = sorted(p for p in (project / "Assets").rglob("*") if p.is_file() and p.suffix in (".txt", ".md") and ("license" in p.name.lower() or "notice" in p.name.lower()))
    if not vrm_licenses:
        raise ValueError("UniVRM license files missing")
    (licenses / "vrm.txt").write_text("\n\n".join(str(p.relative_to(project)) + "\n" + p.read_text(encoding="utf-8-sig") for p in vrm_licenses), encoding="utf-8")
    print(f"Prepared {project}; official source hashes verified; licenses ready")


if __name__ == "__main__":
    restore()
