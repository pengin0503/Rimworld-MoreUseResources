"""Validate and package the files needed by RimWorld."""
import argparse
from pathlib import Path
import re
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parent.parent
INSTALL_DIR = "Rimworld-MoreUseResources"
MOD_XML = Path("About/About.xml")
DLL = Path("Assemblies/MoreUseResources.dll")
TRANSLATIONS = [Path(f"Languages/{lang}/Keyed/MoreUseResources.xml") for lang in ("English", "Japanese")]


def read_translation(path: Path) -> dict[str, list[str]]:
    root = ElementTree.parse(ROOT / path).getroot()
    keys = {}
    if root.tag != "LanguageData":
        raise ValueError(f"{path}: expected LanguageData")
    for element in root:
        if element.tag in keys or not (element.text or "").strip():
            raise ValueError(f"{path}: duplicate or empty key {element.tag}")
        keys[element.tag] = sorted(re.findall(r"\{[0-9]+\}", element.text))
    return keys


def package(destination: Path) -> None:
    about = ElementTree.parse(ROOT / MOD_XML).getroot()
    if about.findtext("packageId") != "pengin0503.moreuseresources" or about.findtext("supportedVersions/li") != "1.7":
        raise ValueError("Unexpected About.xml identity or RimWorld version")
    if read_translation(TRANSLATIONS[0]) != read_translation(TRANSLATIONS[1]):
        raise ValueError("English and Japanese translation keys or placeholders differ")
    with (ROOT / DLL).open("rb") as assembly:
        if assembly.read(2) != b"MZ":
            raise ValueError("The mod DLL is not a PE assembly")

    destination.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(destination, "w", compression=ZIP_DEFLATED) as archive:
        for path in (MOD_XML, DLL, *TRANSLATIONS, Path("README.md")):
            archive.write(ROOT / path, f"{INSTALL_DIR}/{path.as_posix()}")
    print(destination.resolve())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", type=Path)
    package(parser.parse_args().zip)
