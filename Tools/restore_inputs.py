"""Restore the pinned source, font and managed parser used by the player."""
import hashlib
import io
import zipfile
from pathlib import Path
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
UFBX = "https://raw.githubusercontent.com/ufbx/ufbx/83bc7cf44f76bc8622de63b809a42b5d557cd733/"
FONT = "https://raw.githubusercontent.com/google/fonts/a24c920263576ec723d64c1b26f8afabb841601d/ofl/mplus1p/"
INPUTS = (
    ("Native/ufbx.c", UFBX + "ufbx.c", "b06f17dc6ddfa8174903749151182a07b2666cc09e2e43af59363ddd2432e03a"),
    ("Native/ufbx.h", UFBX + "ufbx.h", "1f2a99608749e4d5d57fb924c79dbd9190a4c3c89c55219e436d20427da46fb4"),
    ("Assets/AvaAvaDance/Resources/MPLUS1p-Regular.ttf", FONT + "MPLUS1p-Regular.ttf",
     "2f294ad496432b1608f070d310e3aa2adcf1de4af429f4901df97ec4bd361ed1"),
)


def restore_bundle_reader():
    path = ROOT / "Assets/Plugins/AssetsTools.NET.dll"
    digest = "6b03d589163623b67620fbbc6f425dcb47f856584bd3ab1a34c896b2666372e4"
    if path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest() == digest:
        return
    url = "https://api.nuget.org/v3-flatcontainer/assetstools.net/3.0.5/assetstools.net.3.0.5.nupkg"
    with urlopen(url, timeout=60) as response:
        package = response.read()
    if hashlib.sha256(package).hexdigest() != "e3b79ad8271aa8d84df541ddecba2402448408fd648b33b6a3c2e2a9a1c1d384":
        raise ValueError("Bundle reader package checksum mismatch")
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        library = archive.read("lib/netstandard2.0/AssetsTools.NET.dll")
    if hashlib.sha256(library).hexdigest() != digest:
        raise ValueError("Bundle reader checksum mismatch")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(library)


def restore():
    restore_bundle_reader()
    for relative, url, digest in INPUTS:
        path = ROOT / relative
        if path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest() == digest:
            continue
        with urlopen(url, timeout=60) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != digest:
            raise ValueError(f"Checksum mismatch: {relative}")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        print(f"Restored {relative}")


if __name__ == "__main__":
    restore()
