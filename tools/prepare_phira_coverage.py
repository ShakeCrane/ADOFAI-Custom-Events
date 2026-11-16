#!/usr/bin/env python3
"""Generate a reference coverage exporter in an isolated Phira-Pro checkout.

Only new probe files are written. The copied mask gets a read-only accessor;
production renderer and mask files remain unchanged.
"""
import argparse
from pathlib import Path
import subprocess

COMMIT = "c8fb11c6a41faaf1233a5affabf87b0223e2edd8"
HELPER = """impl Masks {
    pub(super) fn pev_probe_outputs(&self) -> (&[u8], &[u8], &[u8], &[u8], &[u8], &[u8]) {
        (&self.layers[4], &self.layers[5], &self.raw_disabled_green, &self.ready_green, &self.enabled_compose, &self.base_rgba)
    }
"""
PROBE = """    next_frame().await;
    if std::env::var_os("PEV_COVERAGE_PROBE").is_some() {
        let mut first = vec![zone(-0.2,0.05,0.45,0.27,false,true), zone(0.3,-0.1,0.35,0.18,true,true), zone(0.05,0.1,0.28,0.22,false,false), zone(-0.3,-0.12,0.24,0.20,true,false), zone(0.05,-0.05,0.21,0.13,false,false), zone(0.15,0.08,0.17,0.14,true,false)];
        first[0].angle=0.23; first[2].opacity=0.35; first[3].opacity=0.65; first[4].ready=true; first[5].ready=true; first[5].opacity=0.4;
        let mut faded = vec![zone(0.,0.,0.45,0.25,false,true), zone(0.15,0.05,0.32,0.17,false,true), zone(0.1,0.,0.22,0.18,true,true), zone(0.1,0.,0.22,0.18,true,true), zone(0.1,0.,0.22,0.18,true,true), zone(-0.1,0.08,0.27,0.16,false,false)];
        faded[0].opacity=0.37; faded[0].angle=-0.31; faded[1].opacity=0.67; faded[1].angle=0.15; faded[5].opacity=0.52; faded[5].ready=true;
        std::fs::create_dir_all("target/pev-coverage").unwrap();
        for (case,areas) in [("",first),("faded-",faded)] { for (w,h) in [(256,144),(255,143)] {
            let mut masks = reference_mask::Masks::default(); masks.capture_sources=true;
            masks.render_displaced(w,h,16./9.,&areas,1.);
            let prefix=format!("target/pev-coverage/{case}{w}x{h}");
            std::fs::write(format!("{prefix}-sources.bin"), &masks.sources_rgba).unwrap();
            std::fs::write(format!("{prefix}-aux-compose.bin"), &masks.aux_rgba).unwrap();
            let (r0,r1,dg,rg,active,base)=masks.pev_probe_outputs();
            for (suffix,data) in [("ready-normal",r0),("ready-inverse",r1),("disabled-green",dg),("ready-green",rg),("active-compose",active),("base",base)] { std::fs::write(format!("{prefix}-{suffix}.bin"),data).unwrap(); }
            for (name,time) in [("0",0.),("1",1.),("9",9.)] {
                masks.render_displaced(w,h,16./9.,&areas,time);
                std::fs::write(format!("{prefix}-effect-{name}.bin"), &masks.rgba).unwrap();
            }
            let mut touch = reference_touch::TouchMask::default();
            let a = (7_u64, vec2(0.5,0.5)); let b = (42_u64, vec2(0.55,0.5));
            let write_touch = |state: &reference_touch::TouchMask, name: &str| {
                let (bw,bh)=(masks.width/2,masks.height/2);
                let mut result=vec![0_u8;masks.width*masks.height];
                for y in 0..bh { for x in 0..bw {
                    let value=state.sample(vec2((x as f32+0.5)/bw as f32,(y as f32+0.5)/bh as f32),w as f32/h as f32);
                    for dy in 0..2 { for dx in 0..2 { result[(y*2+dy)*masks.width+x*2+dx]=value; }}
                }}
                std::fs::write(format!("{prefix}-touch-{name}.bin"), result).unwrap();
            };
            touch.update_fingers(&[a],0.); touch.update_fingers(&[a],0.05); write_touch(&touch,"half");
            touch.update_fingers(&[a,b],0.1); touch.update_fingers(&[a,b],0.2); write_touch(&touch,"two");
            touch.update_fingers(&[b],0.25); touch.update_fingers(&[b],0.3); write_touch(&touch,"release");
            println!("coverage fixture {case}{w}x{h}: mask {}x{}",masks.width,masks.height);
        }}
        return;
    }
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("checkout", type=Path)
    args = ap.parse_args()
    root = args.checkout.resolve()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if head != COMMIT:
        raise SystemExit(f"Expected {COMMIT}, got {head}")
    if subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "prpr/src/core"], cwd=root).returncode:
        raise SystemExit("Production core differs from pinned commit")
    example = root / "prpr/examples/pev_block_area_gpu.rs"
    if not example.exists():
        raise SystemExit("Run tools/prepare_phira_reference.py on this checkout first")
    mask = subprocess.check_output(["git", "show", f"{COMMIT}:prpr/src/core/block_mask.rs"], cwd=root, text=True)
    driver = example.read_text(encoding="utf-8")
    if mask.count("impl Masks {") != 1 or driver.count("    next_frame().await;\n") != 1:
        raise SystemExit("Unexpected reference source structure")
    old = '#[path = "../src/core/block_mask.rs"]'
    if driver.count(old) != 1:
        raise SystemExit("Unexpected mask module path")
    mask = mask.replace("impl Masks {", HELPER, 1)
    driver = driver.replace(old, '#[path = "../src/core/pev_probe_mask.rs"]', 1)
    driver = driver.replace("    next_frame().await;\n", PROBE, 1)
    targets = ((root / "prpr/src/core/pev_probe_mask.rs", mask),
               (root / "prpr/examples/pev_coverage_probe.rs", driver))
    for path, _ in targets:
        if path.exists():
            raise SystemExit(f"Refusing to overwrite {path}")
    for path, content in targets:
        with path.open("x", encoding="utf-8", newline="\n") as f:
            f.write(content)
        print(f"Created {path}")
    print("Set PEV_COVERAGE_PROBE=1, then cargo run --locked -p prpr --example pev_coverage_probe")
    print("Raw fixture bytes are written under target/pev-coverage; other checks are skipped.")


if __name__ == "__main__":
    main()
