"""Fetch the pinned FBX parser and Japanese font for a local application build."""
import hashlib
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


def restore():
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
