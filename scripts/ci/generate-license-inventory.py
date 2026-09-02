#!/usr/bin/env python3
"""Generate a deterministic SPDX 2.3 inventory from restored NuGet assets."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def packages_from_assets(root: Path) -> list[tuple[str, str]]:
    packages: set[tuple[str, str]] = set()
    for assets_path in sorted(root.glob("**/obj/project.assets.json")):
        data = json.loads(assets_path.read_text(encoding="utf-8"))
        for key, details in data.get("libraries", {}).items():
            if details.get("type") != "package" or "/" not in key:
                continue
            name, version = key.rsplit("/", 1)
            packages.add((name, version))
    return sorted(packages, key=lambda item: (item[0].casefold(), item[1]))


def declared_license(nuget_root: Path, name: str, version: str) -> tuple[str, str | None]:
    package_dir = nuget_root / name.casefold() / version.casefold()
    nuspecs = sorted(package_dir.glob("*.nuspec"))
    if not nuspecs:
        return "NOASSERTION", "NuGet package metadata was unavailable during inventory generation."
    root = ET.parse(nuspecs[0]).getroot()
    license_element = next((element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "license"), None)
    if license_element is not None and license_element.attrib.get("type", "").casefold() == "expression":
        expression = (license_element.text or "").strip()
        if expression:
            return expression, None
    license_url = next((element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "licenseUrl"), None)
    if license_url is not None and (license_url.text or "").strip():
        return "NOASSERTION", f"License metadata URL: {(license_url.text or '').strip()}"
    return "NOASSERTION", "The package did not declare an SPDX license expression in its NuGet metadata."


def spdx_id(name: str, version: str) -> str:
    safe_name = re.sub(r"[^A-Za-z0-9.-]", "-", name)
    digest = hashlib.sha256(f"{name}/{version}".encode()).hexdigest()[:12]
    return f"SPDXRef-Package-{safe_name}-{digest}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--nuget-root", type=Path, default=Path.home() / ".nuget" / "packages")
    args = parser.parse_args()

    packages = packages_from_assets(args.root)
    if not packages:
        parser.error("no restored NuGet packages were found; run dotnet restore first")

    package_records = []
    relationships = []
    namespace_source = []
    for name, version in packages:
        identifier = spdx_id(name, version)
        license_expression, comment = declared_license(args.nuget_root, name, version)
        record = {
            "SPDXID": identifier,
            "name": name,
            "versionInfo": version,
            "downloadLocation": f"https://www.nuget.org/packages/{name}/{version}",
            "filesAnalyzed": False,
            "licenseConcluded": "NOASSERTION",
            "licenseDeclared": license_expression,
            "copyrightText": "NOASSERTION",
            "externalRefs": [{
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": f"pkg:nuget/{name}@{version}",
            }],
        }
        if comment:
            record["comment"] = comment
        package_records.append(record)
        relationships.append({
            "spdxElementId": "SPDXRef-DOCUMENT",
            "relationshipType": "DESCRIBES",
            "relatedSpdxElement": identifier,
        })
        namespace_source.append(f"{name}/{version}")

    digest = hashlib.sha256("\n".join(namespace_source).encode()).hexdigest()
    document = {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": "Drop Shelf third-party NuGet dependency inventory",
        "documentNamespace": f"https://github.com/rwrife/drop-shelf/spdx/nuget-{digest}",
        "creationInfo": {
            "created": "2000-01-01T00:00:00Z",
            "creators": ["Tool: Drop Shelf generate-license-inventory.py"],
        },
        "packages": package_records,
        "relationships": relationships,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"wrote {args.output.name} with {len(package_records)} packages")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
