use std::{env, path::PathBuf, process::Command};

fn main() {
    for path in [
        "../Shared/ToolManifest.cs",
        "../RiMCP/About/About.xml",
        "Manifest/Export.csproj",
        "Manifest/Program.cs",
    ] {
        println!("cargo::rerun-if-changed={path}");
    }
    let output =
        PathBuf::from(env::var_os("OUT_DIR").expect("Cargo sets OUT_DIR")).join("manifest.json");
    let result = Command::new("dotnet")
        .args([
            "run",
            "--project",
            "Manifest/Export.csproj",
            "--configuration",
            "Release",
            "--no-launch-profile",
            "--",
            "../RiMCP/About/About.xml",
        ])
        .arg(output)
        .output()
        .expect("Generating the tool manifest requires the .NET SDK");
    assert!(
        result.status.success(),
        "Tool manifest generation failed:\n{}\n{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
}
