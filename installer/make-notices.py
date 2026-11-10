"""Writes THIRD-PARTY-NOTICES.txt (repository root) from the license files of what the app ships.

Run after adding or upgrading a package, after a new .NET SDK, or after a new bundled 7-Zip, from the repository root,
once the app has been restored for Windows (dotnet restore Ui -r win-x64) and, ideally, published by build.ps1:

    python -I installer/make-notices.py

It reads the package versions from the GUI's obj/project.assets.json (the CLI ships no packages of its own) and the
license texts from the NuGet cache, and stops if a shipped package has no entry below, so nothing slips into the
installer without its notice. The .NET runtime version is the one in the published app (artifacts/publish), or the
newest 10.x runtime pack in the NuGet cache before the first build. build.ps1 runs it with --check after publishing,
which fails when the file is out of date.
"""

import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Ui" / "obj" / "project.assets.json"
DEPS = ROOT / "artifacts" / "publish" / "CompressorGui.deps.json"
SEVEN_ZIP = ROOT / "third-party" / "7zip"
LICENSES = ROOT / "installer" / "licenses"
OUT = ROOT / "THIRD-PARTY-NOTICES.txt"
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
RUNTIME_MAJOR = "10."

# Packages that are not shipped: build-time only, debug only, or native assets for other platforms.
NOT_SHIPPED = {
    "Avalonia.BuildServices",
    "AvaloniaUI.DiagnosticsSupport",
    "HarfBuzzSharp.NativeAssets.Linux", "HarfBuzzSharp.NativeAssets.macOS", "HarfBuzzSharp.NativeAssets.WebAssembly",
    "SkiaSharp.NativeAssets.Linux", "SkiaSharp.NativeAssets.macOS", "SkiaSharp.NativeAssets.WebAssembly",
}

# Component name, the packages it covers, license, copyright.
COMPONENTS = [
    ("Avalonia", ["Avalonia", "Avalonia.Desktop", "Avalonia.Fonts.Inter", "Avalonia.FreeDesktop", "Avalonia.FreeDesktop.AtSpi",
                  "Avalonia.HarfBuzz", "Avalonia.Native", "Avalonia.Remote.Protocol", "Avalonia.Skia",
                  "Avalonia.Themes.Fluent", "Avalonia.Win32", "Avalonia.X11"],
     "MIT", "Copyright 2013-2026 © The AvaloniaUI Project"),
    ("ANGLE (Avalonia.Angle.Windows.Natives)", ["Avalonia.Angle.Windows.Natives"], "BSD-3-Clause",
     "Copyright 2018 The ANGLE Project Authors"),
    ("Inter typeface (in Avalonia.Fonts.Inter)", [], "SIL OFL 1.1",
     "Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)"),
    ("SkiaSharp and HarfBuzzSharp", ["SkiaSharp", "SkiaSharp.NativeAssets.Win32", "HarfBuzzSharp", "HarfBuzzSharp.NativeAssets.Win32"],
     "MIT", "Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation"),
    ("CommunityToolkit.Mvvm", ["CommunityToolkit.Mvvm"], "MIT", "Copyright (c) .NET Foundation and Contributors"),
    ("Bridgewiz.AppUpdate", ["Bridgewiz.AppUpdate", "Bridgewiz.AppUpdate.Avalonia"], "MIT", "Copyright (c) 2026 Mustafa Can Yücel"),
    ("MicroCom.Runtime", ["MicroCom.Runtime"], "MIT", "Copyright 2021 © Nikita Tsukanov"),
    ("Tmds.DBus.Protocol", ["Tmds.DBus.Protocol"], "MIT", "Copyright Tom Deseyn"),
    ("Microsoft.Extensions abstractions", ["Microsoft.Extensions.DependencyInjection.Abstractions",
                                           "Microsoft.Extensions.Logging.Abstractions"],
     "MIT", "Copyright (c) .NET Foundation and Contributors"),
    ("Microsoft.IO.RecyclableMemoryStream", ["Microsoft.IO.RecyclableMemoryStream"], "MIT", "Copyright (c) Microsoft Corporation"),
    (".NET runtime", [], "MIT", "Copyright (c) .NET Foundation and Contributors"),
    # Not a package: the unmodified console build, run as a separate program (third-party/7zip).
    ("7-Zip (7zip\\win-x64\\7za.exe)", [], "LGPL-7-Zip", "Copyright (C) 1999-2026 Igor Pavlov"),
]

LICENSE_NAMES = {
    "MIT": "MIT License",
    "BSD-3-Clause": "BSD 3-Clause License",
    "SIL OFL 1.1": "SIL Open Font License 1.1",
    "LGPL-7-Zip": "GNU LGPL 2.1 or later, some parts BSD 3-Clause (see the 7-Zip license below)",
}

RULE = "=" * 100


def package_dir(name, version):
    return NUGET / name.lower() / version.lower()


def read(path):
    return Path(path).read_text(encoding="utf-8-sig").replace("\r\n", "\n").strip("\n")


def version_key(name):
    return [int(x) if x.isdigit() else 0 for x in name.replace("-", ".").split(".")]


def runtime_pack():
    """The runtime pack folder of the .NET runtime the published app carries."""
    folder = NUGET / "microsoft.netcore.app.runtime.win-x64"
    if DEPS.exists():
        deps = json.loads(DEPS.read_text(encoding="utf-8"))
        for key in deps.get("libraries", {}):
            name, _, version = key.partition("/")
            if name == "runtimepack.Microsoft.NETCore.App.Runtime.win-x64":
                return folder / version
    versions = [p for p in folder.iterdir() if p.is_dir() and p.name.startswith(RUNTIME_MAJOR)]
    return max(versions, key=lambda p: version_key(p.name))


def notices():
    assets = json.loads(ASSETS.read_text(encoding="utf-8"))
    shipped = {}
    for key, lib in assets["libraries"].items():
        if lib["type"] == "package":
            name, version = key.split("/")
            if name not in NOT_SHIPPED:
                shipped[name] = version

    covered = {p for _, packages, _, _ in COMPONENTS for p in packages}
    missing = sorted(set(shipped) - covered)
    if missing:
        sys.exit("No notice entry for shipped package(s): " + ", ".join(missing) + ". Add them to COMPONENTS.")

    runtime = runtime_pack()
    seven_zip_version = read(SEVEN_ZIP / "VERSION")

    def versions(packages):
        return ", ".join(sorted({shipped[p] for p in packages if p in shipped}))

    def version_of(name, packages):
        if name == ".NET runtime":
            return runtime.name
        if name.startswith("7-Zip"):
            return seven_zip_version
        return versions(packages)

    lines = [
        "COMPRESSOR - THIRD-PARTY SOFTWARE NOTICES",
        "",
        "Compressor includes the open-source components listed below. Each is used under the license named with it.",
        "The license texts follow the list, then the notices the components ship themselves.",
        "",
        "COMPONENTS",
        "",
    ]
    for name, packages, license_id, copyright_line in COMPONENTS:
        version = version_of(name, packages)
        lines.append(f"  {name}{' ' + version if version else ''}")
        lines.append(f"      License: {LICENSE_NAMES[license_id]}")
        lines.append(f"      {copyright_line}")
        lines.append("")

    lines += [RULE, "MIT LICENSE", RULE, "",
              "Applies to the components above marked MIT, each under its own copyright notice:", ""]
    lines += [f"  {c}" for c in dict.fromkeys(c for _, _, l, c in COMPONENTS if l == "MIT")]
    lines += ["", read(LICENSES / "MIT.txt"), ""]

    lines += [RULE, "BSD 3-CLAUSE LICENSE (ANGLE, in Avalonia.Angle.Windows.Natives)", RULE, "",
              read(package_dir("Avalonia.Angle.Windows.Natives", shipped["Avalonia.Angle.Windows.Natives"]) / "LICENSE"), ""]

    # The standard text, kept here: Avalonia.Fonts.Inter ships none.
    lines += [RULE, "SIL OPEN FONT LICENSE 1.1 (Inter typeface)", RULE, "",
              "Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)", "",
              "This Font Software is licensed under the SIL Open Font License, Version 1.1.", "",
              read(LICENSES / "OFL-1.1.txt"), ""]

    lines += [RULE, f"7-ZIP {seven_zip_version} LICENSE", RULE, "",
              "7za.exe is the unmodified console build from the official 7-Zip release, run as a separate program.",
              f"Its source code is available at https://www.7-zip.org and https://github.com/ip7z/7zip/releases",
              f"(7z{seven_zip_version.replace('.', '')}-src.7z). The same license ships next to it as 7zip\\License.txt.", "",
              read(SEVEN_ZIP / "License.txt"), ""]

    shipped_notices = [
        ("SkiaSharp and HarfBuzzSharp native libraries (Skia, HarfBuzz and the libraries they include)",
         package_dir("SkiaSharp.NativeAssets.Win32", shipped["SkiaSharp.NativeAssets.Win32"]) / "THIRD-PARTY-NOTICES.txt"),
        ("CommunityToolkit.Mvvm", package_dir("CommunityToolkit.Mvvm", shipped["CommunityToolkit.Mvvm"]) / "ThirdPartyNotices.txt"),
        (".NET runtime", runtime / "THIRD-PARTY-NOTICES.TXT"),
    ]
    for title, path in shipped_notices:
        lines += [RULE, f"NOTICES SHIPPED WITH {title.upper()}", RULE, "", read(path), ""]

    return "\n".join(lines) + "\n", len(shipped), runtime.name, seven_zip_version


def main():
    text, packages, runtime, seven_zip = notices()
    summary = f"{packages} packages, .NET runtime {runtime}, 7-Zip {seven_zip}"
    if "--check" in sys.argv[1:]:
        # Line endings don't count: git may check the file out with CRLF.
        current = OUT.read_text(encoding="utf-8").replace("\r\n", "\n") if OUT.exists() else ""
        if current != text:
            sys.exit(f"{OUT.name} is out of date: run python -I installer/make-notices.py and commit it.")
        print(f"{OUT.name} is up to date ({summary})")
        return
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {OUT} ({summary})")


if __name__ == "__main__":
    main()
