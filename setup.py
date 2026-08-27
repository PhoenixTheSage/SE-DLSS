#!/usr/bin/env python3
"""
Replaces project GUIDs and renames the solution
Requires Python 3.12 or newer.
"""

import os
import re
import traceback
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
import sys
from typing import Iterator, Tuple

if sys.platform == "win32":
    import winreg

DRY_RUN = False

# Always operate on the template directory, not the process CWD.
# Python 3.14's Windows install manager starts double-clicked scripts in
# C:\Windows\System32, which made this script skip the rename prompt and then
# crash with PermissionError while writing Directory.Build.props.user.
ROOT = Path(__file__).resolve().parent

TEMPLATE_NAME = 'ClientPluginTemplate'

# PascalCase C# identifier: must start with a capital letter.
PT_PROJECT_NAME = r"^[A-Z][A-Za-z0-9_]*$"
RX_PROJECT_NAME = re.compile(PT_PROJECT_NAME)

PROJECT_NAMES = (
    "ClientPlugin",
)

USER_PROPS = "Directory.Build.props.user"

USER_PROPS_TEMPLATE = """<Project>
  <PropertyGroup>
    <!-- Folder containing SpaceEngineers.exe (empty = auto-detect) -->
    <Bin64>{bin64}</Bin64>
    <!-- Folder containing Legacy.exe (empty = auto-detect next to Bin64 or %AppData%\\Pulsar) -->
    <Pulsar>{pulsar}</Pulsar>
  </PropertyGroup>
</Project>
"""


def _generate_guid() -> str:
    return str(uuid.uuid4())


def _read_line(prompt: str) -> str:
    try:
        return input(prompt)
    except EOFError:
        print()
        return ""


def _pause(message: str = "Done. (Press Enter to exit)") -> None:
    _read_line(message)


def _replace_text_in_file(replacements: dict[str, str], path: str) -> None:
    is_project = (
        path.endswith(".sln") or path.endswith(".csproj") or path.endswith(".shproj")
    )
    encoding = "utf-8-sig" if is_project else "utf-8"

    with open(path, "rt", encoding=encoding) as f:
        text = f.read()

    original = text

    for k, v in replacements.items():
        text = text.replace(k, v)

    if DRY_RUN or text == original:
        return

    with open(path, "wt", encoding=encoding) as f:
        f.write(text)


def _input_plugin_name() -> str:
    print("Name of the plugin in CapitalizedWords format (C# identifier).")
    print("Examples: MyCoolPlugin, SeDlss, DlssPlugin")
    print("Hyphens and spaces are not allowed. Press Enter to skip renaming.")

    while True:
        plugin_name = _read_line("Plugin name: ").strip()
        if not plugin_name:
            return ""

        if RX_PROJECT_NAME.match(plugin_name):
            return plugin_name

        print(
            f"Invalid plugin name {plugin_name!r}. "
            f"It must match {PT_PROJECT_NAME} (e.g. SeDlss, not SE-DLSS)."
        )


def _input_question(prompt: str, default: bool | None = None) -> bool:
    while True:
        response = _read_line(prompt).lower().strip()

        if default is not None and len(response) == 0:
            return default

        if response in ["n", "no"]:
            return False

        if response in ["y", "yes"]:
            return True

        print("Unknown response (Y/N)")


def _rename_project(name: str) -> None:
    replacements = {
        TEMPLATE_NAME: name,
        "A061FC6C-713E-42CD-B413-151AC8A5074C": _generate_guid().upper(),
    }

    def iter_paths() -> Iterator[Tuple[str, str]]:
        print("Solution:")
        for filename in (f'{TEMPLATE_NAME}.sln', f'{TEMPLATE_NAME}.xml'):
            path = ROOT / filename
            if path.exists():
                yield filename, str(path)

        for project_name in PROJECT_NAMES:
            print()
            print(f"{project_name}:")

            for dirpath, _, filenames in os.walk(ROOT / project_name):
                parts = set(Path(dirpath).parts)
                if "obj" in parts or "bin" in parts:
                    continue

                for filename in filenames:
                    ext = filename.rsplit(".")[-1]
                    if ext in ("xml", "xaml", "cs", "sln", "csproj", "shproj"):
                        path = os.path.join(dirpath, filename)
                        yield filename, path

    rename_files: list[tuple[str, str]] = []
    for filename, path in iter_paths():
        print(f"  {filename}")
        _replace_text_in_file(replacements, path)
        if TEMPLATE_NAME in filename:
            rename_files.append((filename, path))

    if not DRY_RUN:
        for filename, path in rename_files:
            dir_path = os.path.dirname(path)
            dst_name = filename.replace(TEMPLATE_NAME, name)
            dst_path = os.path.join(dir_path, dst_name)
            os.rename(path, dst_path)


def _get_windows_steam_path() -> str | None:
    candidates = (
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Valve\Steam", "SteamPath"),
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Valve\Steam", "InstallPath"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath"),
    )

    for hive, key_path, value_name in candidates:
        try:
            with winreg.OpenKey(hive, key_path) as key:
                value, _ = winreg.QueryValueEx(key, value_name)
        except OSError:
            continue

        if isinstance(value, str):
            path = value.strip().strip("\x00")
            if path:
                return path

    return None


def _get_linux_steam_path() -> str | None:
    candidates = []

    for env_name in ("STEAM_DIR", "STEAM_HOME"):
        env_path = os.environ.get(env_name)
        if env_path:
            candidates.append(Path(env_path).expanduser())

    home = Path.home()
    candidates.extend(
        [
            home / ".steam" / "steam",
            home / ".local" / "share" / "Steam",
            home
            / ".var"
            / "app"
            / "com.valvesoftware.Steam"
            / ".local"
            / "share"
            / "Steam",
        ]
    )

    for path in candidates:
        if (path / "steamapps" / "libraryfolders.vdf").is_file():
            return str(path)

    for path in candidates:
        if path.exists():
            return str(path)

    return None


def _get_steam_path() -> str | None:
    if sys.platform == "win32":
        return _get_windows_steam_path()

    return _get_linux_steam_path()


def _parse_valve_key_values(vdf: str) -> dict[str, object]:
    tokens = re.findall(r'"((?:\\.|[^"\\])*)"|([{}])', vdf)
    index = 0

    def decode_value(value: str) -> str:
        return value.replace(r"\\", "\\").replace(r"\"", '"')

    def read_token() -> str:
        nonlocal index
        if index >= len(tokens):
            raise ValueError("Unexpected end of Valve VDF data")

        quoted, brace = tokens[index]
        index += 1
        if brace:
            return brace
        return decode_value(quoted)

    def read_object() -> dict[str, object]:
        result: dict[str, object] = {}

        while index < len(tokens):
            key = read_token()
            if key == "}":
                return result

            value = read_token()
            if value == "{":
                result[key] = read_object()
            elif value == "}":
                raise ValueError("Unexpected closing brace in Valve VDF data")
            else:
                result[key] = value

        return result

    return read_object()


def _read_text(path: Path) -> str:
    for encoding in ("utf-8-sig", "utf-8"):
        try:
            return path.read_text(encoding=encoding)
        except UnicodeDecodeError:
            continue
    return path.read_text(encoding="utf-8", errors="replace")


def _get_install_locations(vdf_path: str, ids: list[str]) -> dict[str, str | None]:
    parsed = _parse_valve_key_values(_read_text(Path(vdf_path)))
    libraryfolders = parsed.get("libraryfolders")
    if not isinstance(libraryfolders, dict):
        raise ValueError(f"Could not parse Steam library folders from {vdf_path}")

    game_drives: dict[str, str | None] = {game_id: None for game_id in ids}

    for folder in libraryfolders.values():
        if not isinstance(folder, dict):
            continue

        apps = folder.get("apps")
        drive = folder.get("path")
        if not isinstance(apps, dict) or not isinstance(drive, str):
            continue

        for game in ids:
            if game in apps:
                game_drives[game] = drive

    game_install: dict[str, str | None] = {}
    for game_id, drive in game_drives.items():
        if drive is None:
            game_install[game_id] = None
            continue

        path = Path(drive) / "steamapps" / f"appmanifest_{game_id}.acf"
        if not path.is_file():
            game_install[game_id] = None
            continue

        manifest = _parse_valve_key_values(_read_text(path))
        app_state = manifest.get("AppState")
        if not isinstance(app_state, dict):
            game_install[game_id] = None
            continue

        install_dir = app_state.get("installdir")
        if not isinstance(install_dir, str):
            game_install[game_id] = None
            continue

        game_install[game_id] = str(Path(drive) / "steamapps" / "common" / install_dir)

    return game_install


def _user_props_path() -> Path:
    return ROOT / USER_PROPS


def _set_prop(group: ET.Element, name: str, value: str) -> None:
    element = group.find(name)
    if element is None:
        element = ET.SubElement(group, name)
    element.text = value


def _detect_pulsar_dir(game_dir: str) -> str:
    candidate = Path(game_dir) / "Pulsar"
    if (candidate / "Legacy.exe").is_file():
        return str(candidate)
    return ""


def _update_props(
    game_dir: str | None = None,
) -> None:
    """Write the detected Bin64/Pulsar paths into the git-ignored local overrides file."""
    if not game_dir:
        return

    bin64_dir = str(Path(game_dir) / "Bin64")
    pulsar_dir = _detect_pulsar_dir(game_dir)
    props_path = _user_props_path()

    if not props_path.is_file():
        props_path.write_text(
            USER_PROPS_TEMPLATE.format(bin64=bin64_dir, pulsar=pulsar_dir),
            encoding="utf-8",
        )
        print(f"Created {props_path}")
        return

    # Keep any other overrides the developer may have added
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    tree = ET.parse(props_path, parser)
    root = tree.getroot()

    group = root.find("PropertyGroup")
    if group is None:
        group = ET.SubElement(root, "PropertyGroup")

    _set_prop(group, "Bin64", bin64_dir)
    if pulsar_dir:
        _set_prop(group, "Pulsar", pulsar_dir)

    tree.write(props_path)
    print(f"Updated {props_path}")


def _detect_space_engineers() -> str | None:
    steam_path = _get_steam_path()
    if steam_path is None:
        print("Could not find Steam install location.")
        return None

    vdf_path = Path(steam_path) / "steamapps" / "libraryfolders.vdf"
    if not vdf_path.is_file():
        print(f"Could not find Steam library list at {vdf_path}")
        return None

    locations = _get_install_locations(str(vdf_path), ["244850"])
    return locations.get("244850")


def main() -> None:
    """Run the setup."""
    os.chdir(ROOT)

    if sys.version_info < (3, 12):
        print(
            f"Warning: Python 3.12+ is required, but this is {sys.version.split()[0]}."
        )

    sln_path = ROOT / f"{TEMPLATE_NAME}.sln"
    if sln_path.is_file():
        plugin_name = _input_plugin_name()

        if plugin_name:
            _rename_project(plugin_name)
        else:
            print("Skipping project rename")
    else:
        print(f"Could not find {sln_path.name}; skipping project rename.")

    if _input_question("Auto-detect the install location of Space Engineers? (Y/N) [Y]: ", True):
        try:
            game_dir = _detect_space_engineers()
        except Exception as exc:
            print(f"Auto-detect failed: {exc}")
            traceback.print_exc()
            game_dir = None

        if game_dir:
            print(f"Found Space Engineers under {game_dir}")
        else:
            print("Could not find Space Engineers install location.")
            manual = _read_line(
                "Enter the Space Engineers folder (the one containing Bin64), or press Enter to skip: "
            ).strip().strip('"')
            game_dir = manual or None

        _update_props(game_dir)
    else:
        print(f"Please add the paths manually to '{_user_props_path()}'")

    _pause()


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        _pause("Setup failed. Press Enter to exit.")
        sys.exit(1)
