"""Stage pinned canonical engine sources and run offline tests on an owned native Linux root."""

import argparse
import glob
import hashlib
import json
import os
from pathlib import Path
import re
import resource
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET


def digest(path):
    return hashlib.file_digest(path.open("rb"), "sha256").hexdigest().upper() if hasattr(hashlib, "file_digest") else hashlib.sha256(path.read_bytes()).hexdigest().upper()


def sha512(path):
    result = hashlib.sha512()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def official_download(url, path, limit):
    # Neither account-backed sources nor user HTTP credentials are used by SDK acquisition.
    def validate(value):
        parsed = urllib.parse.urlsplit(value)
        if (parsed.scheme != "https" or parsed.hostname != "builds.dotnet.microsoft.com"
                or parsed.username or parsed.password or parsed.query or parsed.fragment):
            raise ValueError("SDK downloads require the explicit official Microsoft HTTPS authority")
    validate(url)
    with urllib.request.urlopen(url, timeout=60) as response, path.open("xb") as output:
        validate(response.url)
        count = 0
        for block in iter(lambda: response.read(1024 * 1024), b""):
            count += len(block)
            if count > limit:
                raise ValueError("Official SDK input exceeds the acquisition bound")
            output.write(block)


def select_sdk(source, root, env, args):
    contract = json.loads((source / "global.json").read_text())["sdk"]
    minimum = tuple(map(int, contract["version"].split(".")))
    if minimum[:2] != (10, 0) or contract["rollForward"] != "latestFeature":
        raise ValueError("Review the changed shipping SDK selection contract before acquiring another SDK")
    if args.sdk_receipt:
        receipt_path = args.sdk_receipt.resolve(strict=True)
        owned = receipt_path.parent
        if owned.parent != Path.home() or not re.fullmatch("redux-native-launcher-[a-f0-9]{32}", owned.name):
            raise ValueError("Reused SDK receipt must belong to a retained owned native fixture")
        receipt = json.loads(receipt_path.read_text())
        archive = owned / "sdk.tar.gz"
        if sha512(archive) != receipt["OfficialSha512"]:
            raise ValueError("Retained SDK archive differs from its official SHA512 receipt")
        dotnet = owned / "sdk/dotnet"
        version = subprocess.check_output([str(dotnet), "--version"], cwd=owned, env=env, text=True).strip()
        if (version != receipt["SdkVersion"] or tuple(map(int, version.split(".")))[:2] != minimum[:2]
                or tuple(map(int, version.split("."))) < minimum):
            raise ValueError("Retained SDK executable differs from its verified selection")
        receipt = dict(receipt, ReusedReceipt=str(receipt_path), ReusedReceiptSha256=digest(receipt_path))
    else:
        installed = shutil.which("dotnet")
        listing = subprocess.check_output([installed, "--list-sdks"], env=env, text=True) if installed else ""
        compatible = [tuple(map(int, value)) for value in re.findall(r"^(10)\.(0)\.(\d+) \[", listing, re.M)]
        compatible = [value for value in compatible if value >= minimum]
        if compatible:
            version = ".".join(map(str, max(compatible)))
            dotnet = Path(installed)
            receipt = {"SdkVersion": version, "ExistingInstalledSdk": True, "InstalledSdkInventory": listing}
        else:
            if not args.acquire_sdk:
                raise ValueError("No compatible .NET 10 SDK is installed. Explicit acquisition or a retained SDK receipt is required.")
            index_url = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
            index_path = root / "releases-index.json"
            official_download(index_url, index_path, 4 * 1024 * 1024)
            channel = next(item for item in json.loads(index_path.read_text())["releases-index"] if item["channel-version"] == "10.0")
            releases_path = root / "releases.json"
            official_download(channel["releases.json"], releases_path, 16 * 1024 * 1024)
            version = channel["latest-sdk"]
            if not re.fullmatch(r"10\.0\.\d+", version) or tuple(map(int, version.split("."))) < minimum:
                raise ValueError("Official latest stable SDK does not satisfy the shipping roll-forward contract")
            candidates = [sdk for release in json.loads(releases_path.read_text())["releases"]
                          for sdk in release.get("sdks", [release.get("sdk", {})]) if sdk.get("version") == version]
            sdk = next(item for item in candidates[0]["files"] if item["rid"] == "linux-x64" and item["name"].endswith(".tar.gz"))
            archive = root / "sdk.tar.gz"
            official_download(sdk["url"], archive, 1024 * 1024 * 1024)
            expected = sdk["hash"].lower()
            if not re.fullmatch(r"[a-f0-9]{128}", expected) or sha512(archive) != expected:
                raise ValueError("Official SDK archive SHA512 verification failed")
            directory = root / "sdk"
            directory.mkdir()
            with tarfile.open(archive) as bundle:
                for item in bundle.getmembers():
                    destination = (directory / item.name).resolve()
                    if directory not in destination.parents and destination != directory:
                        raise ValueError("SDK archive member leaves its owned extraction root")
                    if not (item.isfile() or item.isdir()):
                        raise ValueError("SDK archive contains an unsupported link or special object")
                bundle.extractall(directory)
            dotnet = directory / "dotnet"
            receipt = {"SdkVersion": version, "OfficialSha512": expected, "Archive": str(archive),
                       "ArchiveBytes": archive.stat().st_size, "SdkUrl": sdk["url"], "IndexUrl": index_url,
                       "IndexSha256": digest(index_path), "ReleasesUrl": channel["releases.json"],
                       "ReleasesSha256": digest(releases_path), "ExistingInstalledSdk": False}
    receipt.update(Dotnet=str(dotnet), ShippingSdkContract=contract, CertificateGenerationDisabled=True)
    (root / "sdk-receipt.json").write_text(json.dumps(receipt, indent=2))
    env["DOTNET_ROOT"] = str(dotnet.parent)
    return dotnet, receipt


def main():
    started = time.monotonic()
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--filter", default="FullyQualifiedName~NativeLinux_")
    parser.add_argument("--cache", type=Path, action="append", required=True)
    parser.add_argument("--acquire-sdk", action="store_true")
    parser.add_argument("--sdk-receipt", type=Path)
    parser.add_argument("--publish-cli", action="store_true")
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    evidence = args.evidence.resolve(strict=True)
    if not (source.parent / "Documentation/Omnipatching/README.md").is_file():
        raise ValueError("The source must be the explicit launcher checkout")
    if evidence.parent != source.parent / "Library/Omnipatching/launcher-preparation":
        raise ValueError("Evidence must be the maintained runner's checkout-local root")
    root = Path.home() / ("redux-native-launcher-" + uuid.uuid4().hex)
    root.mkdir(mode=0o700)
    root = root.resolve(strict=True)
    staged = root / "staged"
    state = root / "cli-state"
    state.mkdir()
    env = os.environ.copy()
    env.update(DOTNET_CLI_HOME=str(state), DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
               DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="1", DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="1",
               NUGET_CERT_REVOCATION_MODE="offline",
               KSP2REDUX_LINUX_FIXTURE_ROOT=str(root / "fixture"))
    project_source = source / "tests/Ksp2Redux.Tools.Native.Tests/Ksp2Redux.Tools.Native.Tests.csproj"
    inputs = {project_source, source / "Directory.Build.props", source / "Directory.Packages.props", source / "global.json", Path(__file__).resolve()}
    for item in ET.parse(project_source).iter("Compile"):
        matches = glob.glob(str(project_source.parent / item.attrib["Include"]))
        if not matches:
            raise ValueError("A declared native source input was not found: " + item.attrib["Include"])
        for match in matches:
            path = Path(match).resolve(strict=True)
            if source not in path.parents:
                raise ValueError("A native source declaration leaves the launcher checkout")
            inputs.add(path)
    if args.publish_cli:
        for name in ("Cli", "Launcher", "Common", "BundleConversion"):
            for folder, directories, files in os.walk(source / ("src/Ksp2Redux.Tools." + name)):
                directories[:] = [item for item in directories if item not in ("bin", "obj", ".git", ".avalonia-build-tasks")]
                for name in files:
                    path = (Path(folder) / name).resolve(strict=True)
                    if source not in path.parents:
                        raise ValueError("A shipping project input leaves the launcher checkout")
                    inputs.add(path)
    inputs = sorted(inputs)
    pins = []
    for path in inputs:
        relative = path.relative_to(source)
        target = staged / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        expected = digest(path)
        shutil.copyfile(path, target)
        if digest(target) != expected:
            raise ValueError("Staged source hash differs: " + str(relative))
        pins.append({"path": str(relative), "sha256": expected})
    (root / "source-inputs.json").write_text(json.dumps(pins, indent=2))
    # Explicit local feeds prevent reading account-backed sources or contacting remote services.
    linux_cache = Path.home() / ".nuget/packages"
    feeds = [path.resolve(strict=True) for path in args.cache] + [linux_cache.resolve(strict=True)]
    configuration = ET.Element("configuration")
    package_sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(package_sources, "clear")
    for index, feed in enumerate(feeds):
        ET.SubElement(package_sources, "add", key="owned-local-" + str(index), value=str(feed))
    config_path = root / "NuGet.Config"
    ET.ElementTree(configuration).write(config_path, encoding="utf-8", xml_declaration=True)
    project = staged / "tests/Ksp2Redux.Tools.Native.Tests/Ksp2Redux.Tools.Native.Tests.csproj"
    try:
        dotnet, sdk_receipt = select_sdk(source, root, env, args)
    except Exception as failure:
        failed = {"LinuxRoot": str(root), "SourcePins": pins, "ExitCode": 1, "SdkAcquisitionFailure": str(failure)}
        (root / "result.json").write_text(json.dumps(failed, indent=2))
        (evidence / "native-platform.json").write_text(json.dumps(failed, indent=2))
        raise
    (staged / "global.json").write_text(json.dumps({"sdk": {"version": sdk_receipt["SdkVersion"], "rollForward": "disable"}}))
    restore = [str(dotnet), "restore", str(project), "--packages", str(root / "packages"),
               "--configfile", str(config_path), "--disable-parallel", "-p:NuGetAudit=false"]
    test = [str(dotnet), "test", str(project), "--no-restore", "--disable-build-servers",
            "-maxcpucount:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
            "--logger", "trx;LogFileName=launcher.trx", "--results-directory", str(root / "results"),
            "--filter", args.filter, "--verbosity", "minimal"]
    report = {"LinuxRoot": str(root), "SourcePins": pins, "Commands": [restore, test],
              "SdkReceipt": sdk_receipt,
              "RuntimeQualification": "Native Linux canonical engine and coordinator test project targets shipping net10.0. Outcome is recorded separately.",
              "Filesystem": subprocess.check_output(["stat", "-f", "-c", "%T", str(root)], text=True).strip(),
              "Uname": list(os.uname()), "NoRemotePackageSources": True}
    report["LinuxAbiHeaders"] = [{"path": str(path), "sha256": digest(path)} for path in
                                  (Path("/usr/include/linux/stat.h"), Path("/usr/include/asm-generic/fcntl.h"),
                                   Path("/usr/include/x86_64-linux-gnu/bits/fcntl-linux.h"))]
    mounts = []
    for line in Path("/proc/self/mountinfo").read_text().splitlines():
        left, right = line.split(" - ", 1)
        point = left.split()[4]
        if point == "/" or str(root) == point or str(root).startswith(point + "/"):
            mounts.append((len(point), {"MountPoint": point, "MountId": left.split()[0], "Type": right.split()[0]}))
    report["NativeMount"] = max(mounts, key=lambda item: item[0])[1]
    code = 1
    try:
        report["DotnetInfo"] = subprocess.check_output([str(dotnet), "--info"], cwd=staged, env=env, text=True)
        for command in (restore, test):
            code = subprocess.run(command, cwd=staged, env=env, check=False).returncode
            if code:
                break
        if code == 0 and args.publish_cli:
            cli = staged / "src/Ksp2Redux.Tools.Cli/Ksp2Redux.Tools.Cli.csproj"
            properties = ["-p:RuntimeIdentifiers=linux-x64", "-p:UsedAvaloniaProducts=", "-p:NuGetAudit=false"]
            cli_restore = [str(dotnet), "restore", str(cli), "-r", "linux-x64", "--packages", str(root / "packages"),
                           "--configfile", str(config_path), "--disable-parallel"] + properties
            publish = [str(dotnet), "publish", str(cli), "-c", "Release", "-r", "linux-x64", "--self-contained", "true",
                       "--no-restore", "--disable-build-servers", "-maxcpucount:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                       "-o", str(root / "published-cli")] + properties
            report["CliCommands"] = [cli_restore, publish]
            for command in (cli_restore, publish):
                code = subprocess.run(command, cwd=staged, env=env, check=False).returncode
                if code:
                    break
            report["ShippingCliPublishExitCode"] = code
        changed = [pin["path"] for pin in pins if digest(source / pin["path"]) != pin["sha256"]]
        report.update(ExitCode=code, ChangedSourceInputs=changed,
                      DependencyPins=[{"path": str(path.relative_to(root)), "sha256": digest(path)}
                                      for path in sorted((root / "packages").glob("**/*.nupkg"))])
        if changed:
            code = 1
    finally:
        usage = resource.getrusage(resource.RUSAGE_CHILDREN)
        report.update(ElapsedSeconds=time.monotonic() - started, ChildProcessPeakRssKiB=usage.ru_maxrss,
                      ChildUserCpuSeconds=usage.ru_utime, ChildSystemCpuSeconds=usage.ru_stime,
                      ResourceScope="Owned restore, compilation and test children, not engine-only", PagingAllowed=True)
        trx = root / "results/launcher.trx"
        if trx.is_file():
            shutil.copyfile(trx, evidence / "launcher.trx")
        fixture_report = root / "fixture/result.json"
        if fixture_report.is_file():
            report["NativeFixture"] = json.loads(fixture_report.read_text())
        report["NativeFixtureReports"] = [{"path": str(path), "sha256": digest(path), "result": json.loads(path.read_text())}
                                          for path in sorted(root.glob("fixture*/result.json"))]
        (root / "result.json").write_text(json.dumps(report, indent=2))
        (evidence / "native-platform.json").write_text(json.dumps(report, indent=2))
        print(json.dumps({"LinuxRoot": str(root), "ExitCode": code, "NativeReport": str(evidence / "native-platform.json")}))
    return code


if __name__ == "__main__":
    sys.exit(main())
