#!/usr/bin/env python3
"""Create an isolated Phira-Pro frame and mask exporter for cross-API testing."""
import argparse
from pathlib import Path
import subprocess

COMMIT = "c8fb11c6a41faaf1233a5affabf87b0223e2edd8"
PROBE = r'''    if std::env::var_os("PEV_FRAME_PROBE").is_some() {
        let normal = [zone(0.,0.,0.5,0.25,false,true)];
        let mut shifted = [zone(0.25,0.16,0.2,0.08,false,true)];
        shifted[0].angle=0.4;
        let disabled = [zone(0.,0.,0.5,0.25,false,false)];
        let mut ready = disabled.clone(); ready[0].ready=true;
        let mut green = normal.clone(); green[0].color=[84./255.,1.,84./255.];
        let hover = normal.clone();
        let palette = normal.clone();
        std::fs::create_dir_all("target/pev-frame").unwrap();
        let (width,height)=(screen_width() as usize,screen_height() as usize);
        set_camera(&res.camera);
        assert!(block_shader::prepare_block_effects(), "reference shader warmup failed");
        for (label,zones,is_disabled) in [("",normal,false),("shifted-",shifted,false),
            ("disabled-",disabled,true),("ready-",ready.clone(),false),("ready-full-",ready,false),
            ("green-",green,false),("hover-",hover,false),("palette-",palette,false)] { for (name,time) in [("0",0.),("1",1.),("9",9.)] {
            let touches = [(101_u64,vec2(0.55,0.48)),(205_u64,vec2(0.43,0.55))];
            if label=="hover-" {
                block_shader::reset_block_effects();
                block_shader::draw_layer_at(&mut res,aspect,&zones,time-0.2,false,&touches);
            }
            if label=="palette-" {
                palette_scene();
                screen_pixels().export_png(&format!("target/pev-frame/{width}x{height}-palette-source.png"));
            } else {
                clear_background(Color::new(25./255.,25./255.,25./255.,1.));
            }
            if label=="ready-full-" {
                block_shader::draw_zones_at(&mut res,aspect,&zones,time);
            } else {
                block_shader::draw_layer_at(&mut res,aspect,&zones,time,is_disabled,if label=="hover-" { &touches } else { &[] });
            }
            screen_pixels().export_png(&format!("target/pev-frame/{width}x{height}-{label}frame-{name}.png"));
            let mut masks=reference_mask::Masks::default();
            masks.render_displaced(width,height,aspect,&zones,time);
            if label=="hover-" {
                let mut touch=reference_touch::TouchMask::default();
                touch.update_fingers(&touches,time-0.2); touch.update_fingers(&touches,time);
                let (bw,bh)=(masks.width/2,masks.height/2);
                for y in 0..bh { for x in 0..bw {
                    let value=touch.sample(vec2((x as f32+0.5)/bw as f32,(y as f32+0.5)/bh as f32),width as f32/height as f32);
                    for dy in 0..2 { for dx in 0..2 { masks.aux_rgba[((y*2+dy)*masks.width+x*2+dx)*4+3]=value; }}
                }}
            }
            std::fs::write(format!("target/pev-frame/{width}x{height}-{label}effect-{name}.bin"),&masks.rgba).unwrap();
            std::fs::write(format!("target/pev-frame/{width}x{height}-{label}aux.bin"),&masks.aux_rgba).unwrap();
            println!("frame fixture {width}x{height} {label}t={name}, masks {}x{}",masks.width,masks.height);
        }}
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
    example = root / "prpr/examples/pev_block_area_gpu.rs"
    if not example.exists():
        raise SystemExit("Run tools/prepare_phira_reference.py first")
    driver = example.read_text(encoding="utf-8")
    marker = "    next_frame().await;\n"
    if driver.count(marker) != 1:
        raise SystemExit("Unexpected reference driver structure")
    driver = driver.replace(marker, marker + PROBE, 1)
    target = root / "prpr/examples/pev_frame_probe_v6.rs"
    with target.open("x", encoding="utf-8", newline="\n") as f:
        f.write(driver)
    print("Created", target)
    print("Set PEV_FRAME_PROBE=1, BLOCK_GPU_WIDTH=256, BLOCK_GPU_HEIGHT=144, BLOCK_GPU_SAMPLES=1 and run")
    print("cargo run --locked -p prpr --example pev_frame_probe_v6")


if __name__ == "__main__":
    main()
