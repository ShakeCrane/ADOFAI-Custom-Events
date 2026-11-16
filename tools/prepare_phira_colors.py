#!/usr/bin/env python3
"""Create an isolated fixture exporter from the pinned Phira-Pro color implementation."""
import argparse
from pathlib import Path
import subprocess

COMMIT = "c8fb11c6a41faaf1233a5affabf87b0223e2edd8"
PROBE = r'''    if std::env::var_os("PEV_COLOR_PROBE").is_some() {
        std::fs::create_dir_all("target/pev-colors").unwrap();
        let mut zones = vec![zone(-0.2,0.05,0.45,0.27,false,true), zone(0.3,-0.1,0.35,0.18,true,true), zone(0.05,0.1,0.28,0.22,false,false), zone(-0.3,-0.12,0.24,0.20,true,false), zone(0.05,-0.05,0.21,0.13,false,false), zone(0.15,0.08,0.17,0.14,true,false)];
        zones[0].angle=0.23; zones[0].color=[0.,1.,0.]; zones[1].color=[0.,0.,1.];
        zones[2].opacity=0.35; zones[2].color=[1.,1.,1.];
        zones[3].opacity=0.65; zones[3].color=[0.2,0.4,0.9];
        zones[4].ready=true; zones[4].color=[0.9,0.2,0.7];
        zones[5].ready=true; zones[5].opacity=0.4; zones[5].color=[0.3,0.9,0.2];
        for (w,h) in [(256,144),(255,143)] {
            let mut masks=reference_mask::Masks::default();
            let mut colors=reference_color::Colors::default();
            for (name,time) in [("0",0.),("1",1.),("9",9.)] {
                masks.render_displaced(w,h,16./9.,&zones,time);
                assert!(colors.update(&masks,16./9.,&zones,time));
                let prefix=format!("target/pev-colors/{w}x{h}");
                std::fs::write(format!("{prefix}-raw-active.bin"),colors.pev_raw()).unwrap();
                std::fs::write(format!("{prefix}-active-{name}.bin"),&colors.rgba[0]).unwrap();
                std::fs::write(format!("{prefix}-disabled.bin"),&colors.rgba[1]).unwrap();
            }
            println!("color fixture {w}x{h}: {}x{}",masks.width,masks.height);
        }
        return;
    }
'''


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("checkout", type=Path)
    root = ap.parse_args().checkout.resolve()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if head != COMMIT:
        raise SystemExit(f"Expected {COMMIT}, got {head}")
    if subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "prpr/src/core"], cwd=root).returncode:
        raise SystemExit("Production core differs from pinned commit")
    example = root / "prpr/examples/pev_coverage_probe.rs"
    if not example.exists():
        raise SystemExit("Run the reference and coverage preparation scripts first")
    source = subprocess.check_output(["git", "show", f"{COMMIT}:prpr/src/core/block_color.rs"], cwd=root, text=True)
    if source.count("impl Colors {") != 1:
        raise SystemExit("Unexpected reference color module")
    source = source.replace("impl Colors {", "impl Colors {\n    pub(super) fn pev_raw(&self) -> &[u8] { &self.raw_active }", 1)
    driver = example.read_text(encoding="utf-8")
    marker = "#[path = \"../src/core/block_touch.rs\"]"
    addition = ('use block_shader::DEFAULT_BLOCK_COLOR;\nuse reference_mask as mask;\n'
                '#[path = "../src/core/pev_probe_color.rs"]\nmod reference_color;\n')
    if driver.count(marker) != 1 or driver.count("    next_frame().await;\n") != 1:
        raise SystemExit("Unexpected reference probe structure")
    driver = driver.replace(marker, addition + marker, 1)
    driver = driver.replace("    next_frame().await;\n", "    next_frame().await;\n" + PROBE, 1)
    targets = ((root / "prpr/src/core/pev_probe_color.rs", source),
               (root / "prpr/examples/pev_color_probe.rs", driver))
    for path, _ in targets:
        if path.exists():
            raise SystemExit(f"Refusing to overwrite {path}")
    for path, content in targets:
        with path.open("x", encoding="utf-8", newline="\n") as f:
            f.write(content)
        print(f"Created {path}")
    print("Set PEV_COLOR_PROBE=1 and run cargo run --locked -p prpr --example pev_color_probe")


if __name__ == "__main__":
    main()
