//! Generates `assets/app.ico` (sleek Fluent `<⚡>` code icon) and compiles `assets/app.rc`
//! via MinGW `windres` so `autoname.exe` has a meaningful Windows Explorer, Taskbar,
//! and System Tray icon.

use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

fn sd_rounded_box(px: f32, py: f32, bx: f32, by: f32, r: f32) -> f32 {
    let dx = px.abs() - bx + r;
    let dy = py.abs() - by + r;
    let ox = dx.max(0.0);
    let oy = dy.max(0.0);
    (ox * ox + oy * oy).sqrt() + dx.max(dy).min(0.0) - r
}

fn dist_segment(px: f32, py: f32, ax: f32, ay: f32, bx: f32, by: f32) -> f32 {
    let pax = px - ax;
    let pay = py - ay;
    let bax = bx - ax;
    let bay = by - ay;
    let denom = (bax * bax + bay * bay).max(1e-6);
    let h = ((pax * bax + pay * bay) / denom).clamp(0.0, 1.0);
    let dx = pax - bax * h;
    let dy = pay - bay * h;
    (dx * dx + dy * dy).sqrt()
}

fn point_in_triangle(px: f32, py: f32, ax: f32, ay: f32, bx: f32, by: f32, cx: f32, cy: f32) -> bool {
    let sign = |x1: f32, y1: f32, x2: f32, y2: f32, x3: f32, y3: f32| {
        (x1 - x3) * (y2 - y3) - (x2 - x3) * (y1 - y3)
    };
    let d1 = sign(px, py, ax, ay, bx, by);
    let d2 = sign(px, py, bx, by, cx, cy);
    let d3 = sign(px, py, cx, cy, ax, ay);
    let has_neg = d1 < 0.0 || d2 < 0.0 || d3 < 0.0;
    let has_pos = d1 > 0.0 || d2 > 0.0 || d3 > 0.0;
    !(has_neg && has_pos)
}

fn sample_icon_pixel(nx: f32, ny: f32) -> [f32; 4] {
    // nx, ny in [-1.0, 1.0], y down
    let d_tile = sd_rounded_box(nx, ny, 0.88, 0.88, 0.34);
    if d_tile > 0.05 {
        return [0.0, 0.0, 0.0, 0.0];
    }

    // Tile background gradient: deep Windows blue -> vibrant cyan
    let t = ((nx + 1.0) * 0.25 + (ny + 1.0) * 0.25).clamp(0.0, 1.0);
    let mut r = 0.0 * (1.0 - t) + 28.0 * t;
    let mut g = 95.0 * (1.0 - t) + 178.0 * t;
    let mut b = 184.0 * (1.0 - t) + 250.0 * t;

    // Subtle inner rim highlight at top edge
    if d_tile > -0.09 && ny < 0.0 {
        let rim = ((-ny) * 0.45).clamp(0.0, 0.45);
        r = r * (1.0 - rim) + 165.0 * rim;
        g = g * (1.0 - rim) + 228.0 * rim;
        b = b * (1.0 - rim) + 255.0 * rim;
    }

    // White code brackets `<` and `>`
    let d_lt1 = dist_segment(nx, ny, -0.34, -0.34, -0.64, 0.0);
    let d_lt2 = dist_segment(nx, ny, -0.64, 0.0, -0.34, 0.34);
    let d_gt1 = dist_segment(nx, ny, 0.34, -0.34, 0.64, 0.0);
    let d_gt2 = dist_segment(nx, ny, 0.64, 0.0, 0.34, 0.34);
    let d_brackets = d_lt1.min(d_lt2).min(d_gt1).min(d_gt2) - 0.085;
    if d_brackets < 0.03 {
        let alpha_b = ((-d_brackets + 0.02) / 0.05).clamp(0.0, 1.0);
        r = r * (1.0 - alpha_b) + 245.0 * alpha_b;
        g = g * (1.0 - alpha_b) + 250.0 * alpha_b;
        b = b * (1.0 - alpha_b) + 255.0 * alpha_b;
    }

    // Golden lightning bolt in the center (`⚡`)
    let in_bolt = point_in_triangle(nx, ny, 0.09, -0.50, -0.22, 0.05, 0.05, 0.05)
        || point_in_triangle(nx, ny, -0.05, -0.05, 0.22, -0.05, -0.09, 0.50);
    if in_bolt {
        let bt = ((ny + 0.5) / 1.0).clamp(0.0, 1.0);
        r = 255.0;
        g = 228.0 * (1.0 - bt) + 178.0 * bt;
        b = 72.0 * (1.0 - bt) + 15.0 * bt;
    }

    let tile_alpha = ((-d_tile + 0.03) / 0.06).clamp(0.0, 1.0);
    [b, g, r, tile_alpha * 255.0]
}

fn render_bgra(size: u32) -> Vec<u8> {
    // Bottom-up DIB rows for ICO format
    let mut buf = Vec::with_capacity((size * size * 4) as usize);
    let sub = 4;
    for y_rev in 0..size {
        let y = size - 1 - y_rev;
        for x in 0..size {
            let mut acc = [0.0f32; 4];
            for sy in 0..sub {
                for sx in 0..sub {
                    let fx = (x as f32 + (sx as f32 + 0.5) / sub as f32) / size as f32;
                    let fy = (y as f32 + (sy as f32 + 0.5) / sub as f32) / size as f32;
                    let nx = fx * 2.0 - 1.0;
                    let ny = fy * 2.0 - 1.0;
                    let s = sample_icon_pixel(nx, ny);
                    for k in 0..4 {
                        acc[k] += s[k];
                    }
                }
            }
            let div = (sub * sub) as f32;
            buf.push((acc[0] / div).clamp(0.0, 255.0) as u8);
            buf.push((acc[1] / div).clamp(0.0, 255.0) as u8);
            buf.push((acc[2] / div).clamp(0.0, 255.0) as u8);
            buf.push((acc[3] / div).clamp(0.0, 255.0) as u8);
        }
    }
    buf
}

fn build_ico_entry_bmp(size: u32) -> Vec<u8> {
    let pixels = render_bgra(size);
    let mask_row_bytes = (((size + 31) / 32) * 4) as usize;
    let mask_bytes = vec![0u8; mask_row_bytes * size as usize];

    let mut out = Vec::with_capacity(40 + pixels.len() + mask_bytes.len());
    // BITMAPINFOHEADER (40 bytes)
    out.extend_from_slice(&40u32.to_le_bytes()); // biSize
    out.extend_from_slice(&(size as i32).to_le_bytes()); // biWidth
    out.extend_from_slice(&((size * 2) as i32).to_le_bytes()); // biHeight (XOR + AND)
    out.extend_from_slice(&1u16.to_le_bytes()); // biPlanes
    out.extend_from_slice(&32u16.to_le_bytes()); // biBitCount
    out.extend_from_slice(&0u32.to_le_bytes()); // biCompression (BI_RGB)
    out.extend_from_slice(&((pixels.len() + mask_bytes.len()) as u32).to_le_bytes()); // biSizeImage
    out.extend_from_slice(&0i32.to_le_bytes()); // biXPelsPerMeter
    out.extend_from_slice(&0i32.to_le_bytes()); // biYPelsPerMeter
    out.extend_from_slice(&0u32.to_le_bytes()); // biClrUsed
    out.extend_from_slice(&0u32.to_le_bytes()); // biClrImportant
    out.extend_from_slice(&pixels);
    out.extend_from_slice(&mask_bytes);
    out
}

fn write_ico_file(path: &Path) {
    let sizes = [64u32, 32u32, 16u32];
    let images: Vec<Vec<u8>> = sizes.iter().map(|&sz| build_ico_entry_bmp(sz)).collect();
    let count = sizes.len() as u16;
    let header_size = 6 + 16 * images.len();

    let mut ico = Vec::new();
    // ICONDIR
    ico.extend_from_slice(&0u16.to_le_bytes()); // reserved
    ico.extend_from_slice(&1u16.to_le_bytes()); // type = ICO
    ico.extend_from_slice(&count.to_le_bytes());

    let mut offset = header_size as u32;
    for (&sz, img) in sizes.iter().zip(images.iter()) {
        let dim = if sz >= 256 { 0u8 } else { sz as u8 };
        ico.push(dim); // bWidth
        ico.push(dim); // bHeight
        ico.push(0); // bColorCount
        ico.push(0); // bReserved
        ico.extend_from_slice(&1u16.to_le_bytes()); // wPlanes
        ico.extend_from_slice(&32u16.to_le_bytes()); // wBitCount
        ico.extend_from_slice(&(img.len() as u32).to_le_bytes()); // dwBytesInRes
        ico.extend_from_slice(&offset.to_le_bytes()); // dwImageOffset
        offset += img.len() as u32;
    }
    for img in images {
        ico.extend_from_slice(&img);
    }
    fs::write(path, ico).expect("failed to write assets/app.ico");
}

fn find_windres() -> PathBuf {
    let mingw = PathBuf::from(r"C:\Program Files (x86)\Dev-Cpp\MinGW64\bin\windres.exe");
    if mingw.is_file() {
        return mingw;
    }
    PathBuf::from("windres.exe")
}

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let assets_dir = manifest_dir.join("assets");
    fs::create_dir_all(&assets_dir).unwrap();

    let ico_path = assets_dir.join("app.ico");
    let rc_path = assets_dir.join("app.rc");
    write_ico_file(&ico_path);
    fs::write(&rc_path, "1 ICON \"app.ico\"\r\n").unwrap();

    let obj_path = out_dir.join("app_res.o");
    let windres = find_windres();
    let mut cmd = Command::new(&windres);
    if let Some(bin_dir) = windres.parent() {
        let old_path = env::var_os("PATH").unwrap_or_default();
        let mut paths: Vec<PathBuf> = vec![bin_dir.to_path_buf()];
        paths.extend(env::split_paths(&old_path));
        if let Ok(joined) = env::join_paths(paths) {
            cmd.env("PATH", joined);
        }
    }
    let status = cmd
        .current_dir(&assets_dir)
        .args([
            "--preprocessor=gcc -E -xc-header -DRC_INVOKED",
            "-O",
            "coff",
            "-i",
            "app.rc",
            "-o",
        ])
        .arg(&obj_path)
        .status()
        .expect("failed to invoke windres.exe");
    assert!(status.success(), "windres.exe failed compiling app.rc");

    println!("cargo:rustc-link-arg-bin=autoname={}", obj_path.display());
}
